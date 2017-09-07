module Types exposing (..)

-- builtin
import Dict exposing (Dict)
import Http
import Dom
import Mouse

-- libs
import Keyboard.Event exposing (KeyboardEvent)


type alias Name = String
type alias FieldName = Name
type alias ParamName = Name
type alias TypeName = Name
type alias LiveValue = (String, TypeName, String)

type ID = ID Int
deID : ID -> Int
deID (ID x) = x

-- There are two coordinate systems. Pos is an absolute position in the
-- canvas. Nodes and Edges have Pos'. VPos is the viewport: clicks occur
-- within the viewport and we map Absolute positions back to the
-- viewport to display in the browser.
type alias Pos = {x: Int, y: Int }
type alias VPos =  {vx: Int, vy: Int }
type alias Position = Mouse.Position

type alias MouseEvent = {pos: VPos, button: Int}
type alias LeftButton = Bool

type NodeType = FunctionCall
              | FunctionDef
              | Datastore
              | Value
              | Page
              | Arg
              | Return

type alias NodeDict = Dict Int Node
type alias Node = { name : Name
                  , id : ID
                  , pos : Pos
                  , tipe : NodeType
                  , liveValue : LiveValue
                  -- for DSes
                  , fields : List (FieldName, TypeName)
                  -- for functions
                  , parameters : List Parameter
                  , arguments : List Argument
                  -- for anonfns
                  , returnID : Maybe ID
                  , argIDs : List ID
                  }

type Argument = Const String
              | Edge ID
              | NoArg

type Hole = ResultHole Node
          | ParamHole Node Parameter Int

type EntryCursor = Creating Pos
                 | Filling Node Hole

type State = Selecting ID
           | Entering EntryCursor
           | Deselected


type Msg
    = NodeClick Node
    | RecordClick MouseEvent
    -- we have the actual node when this is created, but by the time we
    -- use the others the node will be changed
    | EntryInputMsg String
    | EntrySubmitMsg
    | GlobalKeyPress KeyboardEvent
    | FocusResult (Result Dom.Error ())
    | FocusAutocompleteItem (Result Dom.Error ())
    | RPCCallBack (List RPC) (Result Http.Error NodeDict)
    | Initialization

type RPC
    = LoadInitialGraph
    | AddDatastore ID Name Pos
    | AddDatastoreField ID FieldName TypeName
    | AddFunctionCall ID Name Pos
    | AddAnon ID Pos
    | AddValue ID String Pos
    | SetConstant Name (ID, ParamName)
    | SetEdge ID (ID, ParamName)
    | DeleteNode ID
    | ClearArgs ID
    | RemoveLastField ID

type alias Autocomplete = { functions : List Function
                          , completions : List AutocompleteItem
                          , index : Int
                          , value : String
                          , open : Bool
                          , liveValue : Maybe LiveValue
                          , tipe : Maybe TypeName
                          }
type AutocompleteItem = ACFunction Function
                      | ACField FieldName


type alias Model = { nodes : NodeDict
                   , center : Pos
                   , error : (String, Int)
                   , lastMsg : Msg
                   , lastMod : Modification
                   -- these values are serialized via Editor
                   , tempFieldName : FieldName
                   , state : State
                   , complete : Autocomplete
                   }

type AutocompleteMod = Query String
                     | Open Bool
                     | Reset
                     | Clear
                     | Complete String
                     | SelectDown
                     | SelectUp
                     | FilterByLiveValue LiveValue
                     | FilterByParamType TypeName

type Modification = Error String
                  | Select ID
                  | Enter EntryCursor
                  | Deselect
                  | RPC (List RPC)
                  | ModelMod (Model -> Model)
                  | NoChange
                  | AutocompleteMod AutocompleteMod
                  | Many (List Modification)

-- name, type optional
type alias Parameter = { name: Name
                       , tipe: TypeName
                       , optional: Bool
                       , description: String
                       }

type alias Function = { name: Name
                      , parameters: List Parameter
                      , description: String
                      , return_type: String
                      }

type alias Flags =
  { state: Maybe Editor
  , complete: List Function
  }

-- Values that we serialize
type alias Editor = {}

