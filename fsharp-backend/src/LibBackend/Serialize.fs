module LibBackend.Serialize

// Serializing to the DB. Serialization formats and binary conversions are
// stored elsewhere

open System.Runtime.InteropServices
open System.Threading.Tasks
open FSharp.Control.Tasks
open FSharpPlus
open Npgsql.FSharp.Tasks
open Npgsql
open LibBackend.Db
open System.Text.RegularExpressions

open Prelude

module PT = LibBackend.ProgramTypes

// FSTODO inline this file into canvas

// (* -------------------------------------------------------- *)
// (* Moved from op.ml as it touches the DB *)
// (* -------------------------------------------------------- *)
// let is_latest_op_request
//     (client_op_ctr_id : string option) (op_ctr : int) (canvas_id : Uuidm.t) :
//     bool =
//   let client_op_ctr_id =
//     match client_op_ctr_id with
//     | Some s when s = "" ->
//         Uuidm.v `V4 |> Uuidm.to_string
//     | None ->
//         Uuidm.v `V4 |> Uuidm.to_string
//     | Some s ->
//         s
//   in
//   Db.run
//     ~name:"update-browser_id-op_ctr"
//     (* This is "UPDATE ... WHERE browser_id = $1 AND ctr < $2" except
//              * that it also handles the initial case where there is no
//              * browser_id record yet *)
//     "INSERT INTO op_ctrs(browser_id,ctr,canvas_id) VALUES($1, $2, $3)
//              ON CONFLICT (browser_id)
//              DO UPDATE SET ctr = EXCLUDED.ctr, timestamp = NOW()
//                        WHERE op_ctrs.ctr < EXCLUDED.ctr"
//     ~params:
//       [ Db.Uuid (client_op_ctr_id |> Uuidm.of_string |> Option.value_exn)
//       ; Db.Int op_ctr
//       ; Db.Uuid canvas_id ] ;
//   Db.exists
//     ~name:"check-if-op_ctr-is-latest"
//     "SELECT 1 FROM op_ctrs WHERE browser_id = $1 AND ctr = $2"
//     ~params:
//       [ Db.Uuid (client_op_ctr_id |> Uuidm.of_string |> Option.value_exn)
//       ; Db.Int op_ctr ]


// --------------------------------------------------------
// Load serialized data from the DB *)
// --------------------------------------------------------

// let load_all_from_db ~host ~(canvas_id : Uuidm.t) () : Types.tlid_oplists =
//   Db.fetch
//     ~name:"load_all_from_db"
//     "SELECT data FROM toplevel_oplists
//      WHERE canvas_id = $1"
//     ~params:[Uuid canvas_id]
//     ~result:BinaryResult
//   |> Binary_serialization.strs2tlid_oplists

// This is a special `load_*` function that specifically loads toplevels
// via the `rendered_oplist_cache` column on `toplevel_oplists`. This column
// stores a binary-serialized representation of the toplevel after the oplist
// is applied. This should be much faster because we don't have to ship
// the full oplist across the network from Postgres to the OCaml boxes,
// and similarly they don't have to apply the full history of the canvas
// in memory before they can execute the code.

let loadOnlyRenderedTLIDs
  (canvasID : CanvasID)
  (tlids : List<tlid>)
  ()
  : Task<List<PT.Toplevel>> =
  // We specifically only load where `deleted` IS FALSE (even though the column
  // is nullable). This means we will not load undeleted handlers from the
  // cache if we've never written their `deleted` state. This is less
  // efficient, but still correct, as they'll still be loaded via their oplist.
  // It avoids loading deleted handlers that have had their cached version
  // written but never their deleted state, which could be true for some
  // handlers that were touched between the addition of the
  // `rendered_oplist_cache` column and the addition of the `deleted` column.
  Sql.query
    "SELECT tipe, rendered_oplist_cache, pos FROM toplevel_oplists
      WHERE canvas_id = @canvasID
      AND tlid = ANY (@tlids)
      AND deleted IS FALSE
      AND (
           ((tipe = 'handler'::toplevel_type OR tipe = 'db'::toplevel_type)
            AND pos IS NOT NULL)
           OR tipe = 'user_function'::toplevel_type
           OR tipe = 'user_tipe'::toplevel_type)"
  |> Sql.parameters [ "canvasID", Sql.uuid canvasID; "tlids", Sql.idArray tlids ]
  |> Sql.executeAsync
       (fun read -> (read.bytea "rendered_oplist_cache", read.stringOrNone "pos"))
  |> Task.bind
       (fun list ->
         list |> List.map OCamlInterop.toplevelOfCachedBinary |> Task.flatten)


// let load_with_dbs ~host ~(canvas_id : Uuidm.t) ~(tlids : Types.tlid list) () :
//     Types.tlid_oplists =
//   let tlid_params = List.map ~f:(fun x -> Db.ID x) tlids in
//   Db.fetch
//     ~name:"load_with_dbs"
//     "SELECT data FROM toplevel_oplists
//       WHERE canvas_id = $1
//         AND (tlid = ANY (string_to_array($2, $3)::bigint[])
//              OR tipe = 'db'::toplevel_type)"
//     ~params:[Db.Uuid canvas_id; Db.List tlid_params; String Db.array_separator]
//     ~result:BinaryResult
//   |> Binary_serialization.strs2tlid_oplists
//

let fetchReleventTLIDsForHTTP
  (canvasID : CanvasID)
  (path : string)
  (method : string)
  : Task<List<tlid>> =

  // The pattern `$2 like name` is deliberate, to leverage the DB's
  // pattern matching to solve our routing.
  Sql.query
    "SELECT tlid
     FROM toplevel_oplists
     WHERE canvas_id = @canvasID
       AND ((module = 'HTTP'
             AND @path like name
             AND modifier = @method)
         OR tipe <> 'handler'::toplevel_type)"
  |> Sql.parameters [ "path", Sql.string path
                      "method", Sql.string method
                      "canvasID", Sql.uuid canvasID ]
  |> Sql.executeAsync (fun read -> read.tlid "tlid")

// let fetch_relevant_tlids_for_execution ~host ~canvas_id () : Types.tlid list =
//   Db.fetch
//     ~name:"fetch_relevant_tlids_for_execution"
//     "SELECT tlid FROM toplevel_oplists
//       WHERE canvas_id = $1
//       AND tipe <> 'handler'::toplevel_type"
//     ~params:[Db.Uuid canvas_id]
//   |> List.map ~f:(fun l ->
//          match l with
//          | [data] ->
//              Types.id_of_string data
//          | _ ->
//              Exception.internal "Shape of per_tlid oplists")
//
//
// let fetch_relevant_tlids_for_event ~(event : Event_queue.t) ~canvas_id () :
//     Types.tlid list =
//   Db.fetch
//     ~name:"fetch_relevant_tlids_for_event"
//     "SELECT tlid FROM toplevel_oplists
//       WHERE canvas_id = $1
//         AND ((module = $2
//               AND name = $3
//               AND modifier = $4)
//               OR tipe <> 'handler'::toplevel_type)"
//     ~params:
//       [ Db.Uuid canvas_id
//       ; String event.space
//       ; String event.name
//       ; String event.modifier ]
//   |> List.map ~f:(fun l ->
//          match l with
//          | [data] ->
//              Types.id_of_string data
//          | _ ->
//              Exception.internal "Shape of per_tlid oplists")
//
//
// let fetch_relevant_tlids_for_cron_checker ~canvas_id () : Types.tlid list =
//   Db.fetch
//     ~name:"fetch_relevant_tlids_for_cron_checker"
//     "SELECT tlid FROM toplevel_oplists
//       WHERE canvas_id = $1
//       AND module = 'CRON'"
//     ~params:[Db.Uuid canvas_id]
//   |> List.map ~f:(fun l ->
//          match l with
//          | [data] ->
//              Types.id_of_string data
//          | _ ->
//              Exception.internal "Shape of per_tlid oplists")
//
//
// let fetch_tlids_for_all_dbs ~(canvas_id : Uuidm.t) () : Types.tlid list =
//   Db.fetch
//     ~name:"fetch_tlids_for_all_dbs"
//     "SELECT tlid FROM toplevel_oplists
//       WHERE canvas_id = $1
//         AND tipe = 'db'::toplevel_type"
//     ~params:[Db.Uuid canvas_id]
//   |> List.map ~f:(fun l ->
//          match l with
//          | [data] ->
//              Types.id_of_string data
//          | _ ->
//              Exception.internal "Shape of per_tlid oplists")
//
//
let fetchAllTLIDs (canvasID : CanvasID) : Task<List<tlid>> =
  Sql.query
    "SELECT tlid FROM toplevel_oplists
     WHERE canvas_id = @canvasID"
  |> Sql.parameters [ "canvasID", Sql.uuid canvasID ]
  |> Sql.executeAsync (fun read -> read.tlid "tlid")


// let transactionally_migrate_oplist
//     ~(canvas_id : Uuidm.t)
//     ~host
//     ~tlid
//     ~(oplist_f : Types.oplist -> Types.oplist)
//     ~(handler_f :
//        Types.RuntimeT.HandlerT.handler -> Types.RuntimeT.HandlerT.handler)
//     ~(db_f : Types.RuntimeT.DbT.db -> Types.RuntimeT.DbT.db)
//     ~(user_fn_f : Types.RuntimeT.user_fn -> Types.RuntimeT.user_fn)
//     ~(user_tipe_f : Types.RuntimeT.user_tipe -> Types.RuntimeT.user_tipe)
//     () : (string, unit) Tc.Result.t =
//   Log.inspecT "migrating oplists for" (host, tlid) ;
//   try
//     Db.transaction ~name:"oplist migration" (fun () ->
//         let oplist, rendered =
//           Db.fetch
//             ~name:"load_all_from_db"
//             (* SELECT FOR UPDATE locks row! *)
//             "SELECT data, rendered_oplist_cache FROM toplevel_oplists
//          WHERE canvas_id = $1
//          AND tlid = $2
//          FOR UPDATE"
//             ~params:[Uuid canvas_id; ID tlid]
//             ~result:BinaryResult
//           |> List.hd_exn
//           |> function
//           | [data; rendered_oplist_cache] ->
//               ( Binary_serialization.oplist_of_binary_string data
//               , rendered_oplist_cache )
//           | _ ->
//               Exception.internal "invalid oplists"
//         in
//         let try_convert f () = try Some (f rendered) with _ -> None in
//         let rendered =
//           if rendered = ""
//           then Db.Null
//           else
//             try_convert
//               (Binary_serialization.translate_handler_as_binary_string
//                  ~f:handler_f)
//               ()
//             |> Tc.Option.or_else_lazy
//                  (try_convert
//                     (Binary_serialization.translate_db_as_binary_string ~f:db_f))
//             |> Tc.Option.or_else_lazy
//                  (try_convert
//                     (Binary_serialization
//                      .translate_user_function_as_binary_string
//                        ~f:user_fn_f))
//             |> Tc.Option.or_else_lazy
//                  (try_convert
//                     (Binary_serialization.translate_user_tipe_as_binary_string
//                        ~f:user_tipe_f))
//             |> Tc.Option.map ~f:(fun str -> Db.Binary str)
//             |> Tc.Option.or_else_lazy (fun () ->
//                    Exception.internal "none of the decoders worked on the cache")
//             |> Tc.Option.withDefault ~default:Db.Null
//         in
//         let converted_oplist = oplist |> oplist_f in
//         Db.run
//           ~name:"save per tlid oplist"
//           "UPDATE toplevel_oplists
//        SET data = $1,
//            digest = $2,
//            rendered_oplist_cache = $3
//        WHERE canvas_id = $4
//          AND tlid = $5"
//           ~params:
//             [ Binary
//                 (Binary_serialization.oplist_to_binary_string converted_oplist)
//             ; String Binary_serialization.digest
//             ; rendered
//             ; Uuid canvas_id
//             ; ID tlid ]) ;
//     Ok ()
//   with e -> Error (Exception.to_string e)
//
//
//
//
// (* ------------------------- *)
// (* JSON *)
// (* ------------------------- *)
//
// (* ------------------------- *)
// (* hosts *)
// (* ------------------------- *)
// let current_hosts () : string list =
//   Db.fetch ~name:"oplists" "SELECT DISTINCT name FROM canvases" ~params:[]
//   |> List.map ~f:List.hd_exn
//   |> List.filter ~f:(fun h -> not (String.is_prefix ~prefix:"test-" h))
//   |> List.dedup_and_sort ~compare
//
//

// let tier_one_hosts () : string list =
//   [ "ian-httpbin"
//   ; "paul-slackermuse"
//   ; "listo"
//   ; "ellen-battery2"
//   ; "julius-tokimeki-unfollow" ]
//
//
// (* https://stackoverflow.com/questions/15939902/is-select-or-insert-in-a-function-prone-to-race-conditions/15950324#15950324 *)
// let fetch_canvas_id (owner : Uuidm.t) (host : string) : Uuidm.t =
//   let host_length = String.length host in
//   if host_length > 64
//   then
//     Exception.internal
//       (Printf.sprintf "Canvas name was %i chars, must be <= 64." host_length)
//   else
//     Db.fetch_one
//       ~name:"fetch_canvas_id"
//       "SELECT canvas_id($1, $2, $3)"
//       ~params:[Uuid (Util.create_uuid ()); Uuid owner; String host]
//     |> List.hd_exn
//     |> Uuidm.of_string
//     |> Option.value_exn
//
//
// type cron_schedule_data =
//   { canvas_id : Uuidm.t
//   ; owner : Uuidm.t
//   ; host : string
//   ; tlid : string
//   ; name : string
//   ; modifier : string }
//
// (** Fetch cron handlers from the DB. Active here means:
//  * - a non-null interval field in the spec
//  * - not deleted (When a CRON handler is deleted, we set (module, modifier,
//  *   deleted) to (NULL, NULL, True);  so our query `WHERE module = 'CRON'`
//  *   ignores deleted CRONs.)
//  *)
// let fetch_active_crons (span : Span.t) : cron_schedule_data list =
//   Telemetry.with_span span "Serialize.fetch_crons" (fun _ ->
//       Db.fetch
//         ~name:"get crons for scheduler"
//         "SELECT canvas_id,
//                 tlid,
//                 modifier,
//                 toplevel_oplists.name,
//                 toplevel_oplists.account_id,
//                 canvases.name
//          FROM toplevel_oplists
//          JOIN canvases ON toplevel_oplists.canvas_id = canvases.id
//          WHERE module = 'CRON'
//            AND modifier IS NOT NULL
//            AND toplevel_oplists.name IS NOT NULL"
//         ~params:[]
//       |> List.map ~f:(function
//              | [canvas_id; tlid; modifier; name; account_id; host] ->
//                  { canvas_id = canvas_id |> Uuidm.of_string |> Option.value_exn
//                  ; tlid
//                  ; modifier
//                  ; name
//                  ; owner = account_id |> Uuidm.of_string |> Option.value_exn
//                  ; host }
//              | _ ->
//                  Exception.internal
//                    "Wrong shape from get_crons_for_scheduler query"))
//
