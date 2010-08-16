﻿module FsSql

open System
open System.Collections.Generic
open System.Data
open System.Data.SqlClient
open System.Linq
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open FsSqlImpl
open FsSqlPrelude
    
let internal PrintfFormatProc (worker: string * obj list -> 'd)  (query: PrintfFormat<'a, _, _, 'd>) : 'a =
    if not (FSharpType.IsFunction typeof<'a>) then
        unbox (worker (query.Value, []))
    else
        let rec getFlattenedFunctionElements (functionType: Type) =
            let domain, range = FSharpType.GetFunctionElements functionType
            if not (FSharpType.IsFunction range)
                then domain::[range]
                else domain::getFlattenedFunctionElements(range)
        let types = getFlattenedFunctionElements typeof<'a>
        let rec proc (types: Type list) (values: obj list) (a: obj) : obj =
            let values = a::values
            match types with
            | [x;_] -> 
                let result = worker (query.Value, List.rev values)
                box result
            | x::y::z::xs -> 
                let cont = proc (y::z::xs) values
                let ft = FSharpType.MakeFunctionType(y,z)
                let cont = FSharpValue.MakeFunction(ft, cont)
                box cont
            | _ -> failwith "shouldn't happen"
        
        let handler = proc types []
        unbox (FSharpValue.MakeFunction(typeof<'a>, handler))

let internal sqlProcessor (conn: #IDbConnection) (sql: string, values: obj list) =
    let stripFormatting s =
        let i = ref -1
        let eval (rxMatch: Match) =
            incr i
            sprintf "@p%d" !i
        Regex.Replace(s, "%.", eval)
    let sql = stripFormatting sql
    let cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    let createParam i (p: obj) =
        let param = cmd.CreateParameter()
        //param.DbType <- DbType.
        param.ParameterName <- sprintf "@p%d" i
        param.Value <- p
        cmd.Parameters.Add param |> ignore
    values |> Seq.iteri createParam
    cmd

let internal sqlProcessorToDataReader a b = 
    let cmd = sqlProcessor a b
    cmd.ExecuteReader()

let internal sqlProcessorNonQuery a b =
    let cmd = sqlProcessor a b
    cmd.ExecuteNonQuery()

let execReaderF connectionFactory a = PrintfFormatProc (sqlProcessorToDataReader connectionFactory) a
let execNonQueryF connectionFactory a = PrintfFormatProc (sqlProcessorNonQuery connectionFactory) a

type Parameter = {
    DbType: DbType
    Direction: ParameterDirection
    ParameterName: string
    Value: obj
} with
    static member make(parameterName, value) =
        { DbType = Unchecked.defaultof<DbType>
          Direction = ParameterDirection.Input
          ParameterName = parameterName
          Value = value }

let addParameter (cmd: #IDbCommand) (p: Parameter) =
    let par = cmd.CreateParameter()
    par.DbType <- p.DbType
    par.Direction <- p.Direction
    par.ParameterName <- p.ParameterName
    par.Value <- p.Value
    cmd.Parameters.Add par |> ignore
    cmd

let internal prepareCommand (connection: #IDbConnection) (sql: string) (cmdType: CommandType) (parameters: Parameter list) =
    let cmd = connection.CreateCommand()
    cmd.CommandText <- sql
    cmd.CommandType <- cmdType
    parameters |> Seq.iter (addParameter cmd >> ignore)
    cmd

let internal inferParameterDbType (p: string * obj) = 
    Parameter.make(fst p, snd p)

let inferParameterDbTypes (p: (string * obj) list) = 
    p |> List.map inferParameterDbType

let execReader (connection: #IDbConnection) (sql: string) (parameters: Parameter list) =
    use cmd = prepareCommand connection sql CommandType.Text parameters
    cmd.ExecuteReader()

let execNonQuery (connection: #IDbConnection) (sql: string) (parameters: Parameter list) =
    use cmd = prepareCommand connection sql CommandType.Text parameters
    cmd.ExecuteNonQuery()
    
let transactionalWithIsolation (isolation: IsolationLevel) (conn: #IDbConnection) (f: #IDbConnection -> 'a -> 'b) (a: 'a) =
    let tx = conn.BeginTransaction(isolation)
    log "started tx"
    try
        let r = f conn a
        tx.Commit()
        log "committed tx"
        r
    with e ->
        tx.Rollback()
        log "rolled back tx"
        reraise()

let transactional a = 
    transactionalWithIsolation IsolationLevel.Unspecified a

type TxResult<'a> = Success of 'a | Failure of exn

let transactional2 (conn: #IDbConnection) (f: #IDbConnection -> 'a -> 'b) (a: 'a) =
    let tx = conn.BeginTransaction()
    try
        let r = f conn a
        tx.Commit()
        Success r
    with e ->
        tx.Rollback()
        Failure e

let isNull a = DBNull.Value.Equals a

let readField (field: string) (record: #IDataRecord) : 'a option =
    let o = record.[field]
    if isNull o
        then None
        else Some (unbox o)

let readInt : string -> #IDataRecord -> int option = readField 

let readString : string -> #IDataRecord -> string option = readField 

let mapScalar (dr: #IDataReader) =
    try
        if dr.Read()
            then unbox dr.[0]
            else failwith "No results"
    finally
        dr.Dispose()

let execScalar a b c =
    execReader a b c |> mapScalar

let writeOption = 
    function
    | None -> box DBNull.Value
    | Some x -> box x

let findOne mapper query id =
    let r = query id
            |> Seq.ofDataReader
            |> Seq.map mapper
            |> Seq.truncate 1
            |> Seq.toList
    if r.Length = 0
        then None
        else Some r.[0]

let getOne mapper query id =
    query id
    |> Seq.ofDataReader
    |> Seq.map mapper
    |> Enumerable.Single
