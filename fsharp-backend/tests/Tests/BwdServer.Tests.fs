module Tests.BwdServer

open Expecto

open System.Threading.Tasks
open System.Net.Sockets
open System.Text.RegularExpressions
open FSharpPlus

open Prelude

module RT = LibExecution.RuntimeTypes
module PT = LibBackend.ProgramTypes
module Routing = LibBackend.Routing
module Canvas = LibBackend.Canvas

open TestUtils

let t name =
  testTask $"Httpfiles: {name}" {
    let testName = $"test-{name}"
    do! TestUtils.clearCanvasData (CanvasName.create testName)
    let toBytes (str : string) = System.Text.Encoding.ASCII.GetBytes str
    let toStr (bytes : byte array) = System.Text.Encoding.ASCII.GetString bytes

    let setHeadersToCRLF (text : byte array) : byte array =
      // We keep our test files with an LF line ending, but the HTTP spec
      // requires headers (but not the body, nor the first line) to have CRLF
      // line endings
      let mutable justSawNewline = false
      let mutable inBody = false

      text
      |> Array.toList
      |> List.collect
           (fun b ->
             if not inBody && b = byte '\n' then
               if justSawNewline then inBody <- true
               justSawNewline <- true
               [ byte '\r'; b ]
             else
               justSawNewline <- false
               [ b ])
      |> List.toArray

    let filename = $"tests/httptestfiles/{name}"
    let! contents = System.IO.File.ReadAllBytesAsync filename
    let contents = toStr contents

    let request, expectedResponse, httpDefs =
      // TODO: use FsRegex instead
      let options = System.Text.RegularExpressions.RegexOptions.Singleline

      let m =
        Regex.Match(
          contents,
          "^((\[http-handler \S+ \S+\]\n.*\n)+)\[request\]\n(.*)\[response\]\n(.*)$",
          options
        )

      if not m.Success then failwith $"incorrect format in {name}"
      let g = m.Groups

      (g.[3].Value |> toBytes |> setHeadersToCRLF,
       g.[4].Value |> toBytes |> setHeadersToCRLF,
       g.[2].Value)

    let oplists =
      Regex.Matches(httpDefs, "\[http-handler (\S+) (\S+)\]\n(.*)\n")
      |> Seq.toList
      |> List.map
           (fun m ->
             let progString = m.Groups.[3].Value
             let httpRoute = m.Groups.[2].Value
             let httpMethod = m.Groups.[1].Value

             let (source : PT.Expr) =
               progString |> FSharpToExpr.parse |> FSharpToExpr.convertToExpr

             let gid = Prelude.gid

             let ids : PT.Handler.ids =
               { moduleID = gid (); nameID = gid (); modifierID = gid () }

             let h : PT.Handler.T =
               { tlid = gid ()
                 pos = { x = 0; y = 0 }
                 ast = source
                 spec =
                   PT.Handler.HTTP(route = httpRoute, method = httpMethod, ids = ids) }

             (h.tlid,
              [ PT.SetHandler(h.tlid, h.pos, h) ],
              PT.TLHandler h,
              Canvas.NotDeleted))

    let! (meta : Canvas.Meta) = testCanvasInfo testName
    do! Canvas.saveTLIDs meta oplists

    // Web server might not be loaded yet
    use client = new TcpClient()

    let mutable connected = false

    for i in 1 .. 10 do
      try
        if not connected then
          do! client.ConnectAsync("127.0.0.1", 10001)
          connected <- true
      with _ -> do! System.Threading.Tasks.Task.Delay 1000

    use stream = client.GetStream()
    stream.ReadTimeout <- 1000 // responses should be instant, right?

    do! stream.WriteAsync(request, 0, request.Length)

    let length = 10000
    let response = Array.zeroCreate length
    let! byteCount = stream.ReadAsync(response, 0, length)
    let response = Array.take byteCount response

    let response =
      FsRegEx.replace
        "Date: ..., .. ... .... ..:..:.. ..."
        "Date: XXX, XX XXX XXXX XX:XX:XX XXX"
        (toStr response)

    if String.startsWith "_" name then
      skiptest $"underscore test - {name}"
    else
      Expect.equal response (toStr expectedResponse) ""
  }

let testsFromFiles =
  // get all files
  let dir = "tests/httptestfiles/"

  System.IO.Directory.GetFiles(dir, "*")
  |> Array.map (System.IO.Path.GetFileName)
  |> Array.toList
  |> List.map t

let unitTests =
  [ testMany
      "sanitizeUrlPath"
      BwdServer.sanitizeUrlPath
      [ ("//", "/")
        ("/foo//bar", "/foo/bar")
        ("/abc//", "/abc")
        ("/abc/", "/abc")
        ("/abc", "/abc")
        ("/", "/")
        ("/abcabc//xyz///", "/abcabc/xyz")
        ("", "/") ]
    testMany
      "ownerNameFromHost"
      (fun cn ->
        cn
        |> CanvasName.create
        |> LibBackend.Account.ownerNameFromCanvasName
        |> fun (on : OwnerName.T) -> on.ToString())
      [ ("test-something", "test"); ("test", "test"); ("test-many-hyphens", "test") ]
    testMany
      "routeVariables"
      Routing.routeVariables
      [ ("/user/:userid/card/:cardid", [ "userid"; "cardid" ]) ]
    testMany2
      "routeInputVars"
      Routing.routeInputVars
      [ ("/hello/:name", "/hello/alice-bob", Some [ "name", RT.DStr "alice-bob" ])
        ("/hello/alice-bob", "/hello/", None)
        ("/user/:userid/card/:cardid",
         "/user/myid/card/0",
         Some [ "userid", RT.DStr "myid"; "cardid", RT.DStr "0" ])
        ("/a/:b/c/d", "/a/b/c/d", Some [ "b", RT.DStr "b" ])
        ("/a/:b/c/d", "/a/b/c", None)
        ("/a/:b", "/a/b/c/d", Some [ "b", RT.DStr "b/c/d" ])
        ("/:a/:b/:c",
         "/a/b/c/d/e",
         Some [ "a", RT.DStr "a"; "b", RT.DStr "b"; "c", RT.DStr "c/d/e" ])
        ("/a/:b/c/d", "/a/b/c/e", None)
        ("/letters:var", "lettersextra", None) ]
    testManyTask
      "canvasNameFromHost"
      (fun h ->
        h
        |> BwdServer.canvasNameFromHost
        |> Task.map (Option.map (fun cn -> cn.ToString())))
      [ ("test-something.builtwithdark.com", Some "test-something")
        ("my-canvas.builtwithdark.localhost", Some "my-canvas")
        ("builtwithdark.localhost", Some "builtwithdark")
        ("my-canvas.darkcustomdomain.com", Some "my-canvas")
        ("www.microsoft.com", None) ] ]

let tests =
  testList
    "BwdServer"
    [ testList "From files" testsFromFiles; testList "unit tests" unitTests ]

open Microsoft.AspNetCore.Hosting
// run our own webserver instead of relying on the dev webserver
let init () : Task =
  LibBackend.Init.init ()
  (BwdServer.webserver false 10001).RunAsync()


// FSTODO
// let t_result_to_response_works () =
//   let req =
//     Req.make
//       ~headers:(Header.init ())
//       (Uri.of_string "http://test.builtwithdark.com/")
//   in
//   let req_example_com =
//     Req.make
//       ~headers:(Header.of_list [("Origin", "https://example.com")])
//       (Uri.of_string "http://test.builtwithdark.com/")
//   in
//   let req_google_com =
//     Req.make
//       ~headers:(Header.of_list [("Origin", "https://google.com")])
//       (Uri.of_string "http://test.builtwithdark.com/")
//   in
//   let c = ops2c_exn "test" [] in
//   ignore
//     (List.map
//        ~f:(fun (dval, req, cors_setting, check) ->
//          Canvas.update_cors_setting c cors_setting ;
//          dval
//          |> Webserver.result_to_response ~c ~execution_id ~req
//          |> Libcommon.Telemetry.with_root "test" (fun span ->
//                 Webserver.respond_or_redirect span)
//          |> Lwt_main.run
//          |> fst
//          |> check)
//        [ ( exec_ast (record [])
//          , req
//          , None
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "objects get application/json content-type"
//                (Some "application/json; charset=utf-8")
//                (Header.get (Resp.headers r) "Content-Type") )
//        ; ( exec_ast (list [int 1; int 2])
//          , req
//          , None
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "lists get application/json content-type"
//                (Some "application/json; charset=utf-8")
//                (Header.get (Resp.headers r) "Content-Type") )
//        ; ( exec_ast (int 2)
//          , req
//          , None
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "other things get text/plain content-type"
//                (Some "text/plain; charset=utf-8")
//                (Header.get (Resp.headers r) "Content-Type") )
//        ; ( exec_ast (fn "Http::success" [record []])
//          , req
//          , None
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "Http::success gets application/json"
//                (Some "application/json; charset=utf-8")
//                (Header.get (Resp.headers r) "Content-Type") )
//        ; ( exec_ast (int 1)
//          , req
//          , None
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "without any other settings, we get Access-Control-Allow-Origin: *."
//                (Some "*")
//                (Header.get (Resp.headers r) "Access-Control-Allow-Origin") )
//        ; ( DError (SourceNone, "oh no :(")
//          , req
//          , None
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "we get Access-Control-Allow-Origin: * even for errors."
//                (Some "*")
//                (Header.get (Resp.headers r) "Access-Control-Allow-Origin") )
//        ; ( DIncomplete SourceNone
//          , req
//          , None
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "we get Access-Control-Allow-Origin: * even for incompletes."
//                (Some "*")
//                (Header.get (Resp.headers r) "Access-Control-Allow-Origin") )
//        ; ( exec_ast (int 1)
//          , req
//          , Some Canvas.AllOrigins
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "with explicit wildcard setting, we get Access-Control-Allow-Origin: *."
//                (Some "*")
//                (Header.get (Resp.headers r) "Access-Control-Allow-Origin") )
//        ; ( exec_ast (int 1)
//          , req
//          , Some (Canvas.Origins ["https://example.com"])
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "with allowlist setting and no Origin, we get no Access-Control-Allow-Origin"
//                None
//                (Header.get (Resp.headers r) "Access-Control-Allow-Origin") )
//        ; ( exec_ast (int 1)
//          , req_example_com
//          , Some (Canvas.Origins ["https://example.com"])
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "with allowlist setting and matching Origin, we get good Access-Control-Allow-Origin"
//                (Some "https://example.com")
//                (Header.get (Resp.headers r) "Access-Control-Allow-Origin") )
//        ; ( exec_ast (int 1)
//          , req_google_com
//          , Some (Canvas.Origins ["https://example.com"])
//          , fun r ->
//              AT.check
//                (AT.option AT.string)
//                "with allowlist setting and mismatched Origin, we get null Access-Control-Allow-Origin"
//                (Some "null")
//                (Header.get (Resp.headers r) "Access-Control-Allow-Origin") ) ]) ;
//   ()
//
