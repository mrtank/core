﻿// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2018 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

module WebSharper.FSharp.NamedPipeClient

open System.Diagnostics
open System.IO.Pipes
open System.Threading
open System.Runtime.Serialization.Formatters.Binary
open WebSharper.Compiler.WsFscServiceCommon
open System.IO

let (|StdOut|_|) (str: string) =
    if str.StartsWith("n: ") then
        str.Substring(3) |> Some
    else
        None

let (|StdErr|_|) (str: string) =
    if str.StartsWith("e: ") then
        str.Substring(3) |> Some
    else
        None

let (|Finish|_|) (str: string) =
    if str.StartsWith("x: ") then
        let trimmed = 
            str.Substring(3)
        trimmed
        |> System.Int32.Parse
        |> Some
    else
        None

let sendCompileCommand args =
    let serverName = "." // local machine server name
    let location = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location)
#if DEBUG
    printfn "location of wsfsc.exe and wsfscservice.exe: %s" location
#endif
    let pipeName = (location, "WsFscServicePipe") |> System.IO.Path.Combine |> hashPipeName
#if DEBUG
    printfn "pipeName is : %s" pipeName
#endif
    let fileNameOfService = (location, "wsfscservice.exe") |> System.IO.Path.Combine
    let runningServers =
        try
            Process.GetProcessesByName("wsfscservice")
            |> Array.filter (fun x -> System.String.Equals(x.MainModule.FileName, fileNameOfService, System.StringComparison.OrdinalIgnoreCase))
        with
        | e ->
#if DEBUG
            eprintfn "Could not read running processes of wsfscservice. Error : %s" e.Message
#endif
            [||]


#if DEBUG
    printfn "number of running wsfscservices (> 0 means server is not needed): %i" runningServers.Length
#endif
    let isServerNeeded =
        runningServers |> Array.isEmpty

    use waiter = new ManualResetEventSlim(false)
    let mutable returnCode = 0
    let mutable proc: Process = null
    let disposedEventHandler = 
        new System.EventHandler (fun _ _ -> 
            if returnCode = 0 then
#if DEBUG
                eprintfn "Unhandled disposing of Process for wsfscservice"
#endif
                returnCode <- -12211
            waiter.Set()
            )
    let exitedEventHandler =
        new System.EventHandler (fun _ _ ->
            if returnCode = 0 then
#if DEBUG
                eprintfn "Unhandled exiting of Process for wsfscservice"
#endif
                returnCode <- -12211
            waiter.Set()
            )
    if isServerNeeded then
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) then
            let cmdName = (location, "wsfscservice_start.cmd") |> System.IO.Path.Combine
            let startInfo = ProcessStartInfo(cmdName) // ProcessStartInfo(fileNameOfService)
            startInfo.CreateNoWindow <- true
            startInfo.UseShellExecute <- false
            startInfo.WindowStyle <- ProcessWindowStyle.Hidden
            proc <- Process.Start(startInfo)
            proc.Disposed.AddHandler disposedEventHandler
            proc.Exited.AddHandler exitedEventHandler
    use clientPipe = new NamedPipeClientStream( serverName, //server name, local machine is .
                          pipeName, // name of the pipe,
                          PipeDirection.InOut, // direction of the pipe 
                          PipeOptions.WriteThrough // the operation will not return the control until the write is completed
                          ||| PipeOptions.Asynchronous)
    use token = new CancellationTokenSource()
    let Write (bytes: byte array) =
        if clientPipe.IsConnected && clientPipe.CanWrite then
            let write = async {
                let printResponse (bytes: byte array) = 
                    let message = System.Text.Encoding.UTF8.GetString(bytes)
                    match message with
                    | StdOut n -> printfn "%s" n
                    | StdErr e -> eprintfn "%s" e
                    | Finish i -> 
                        returnCode <- i
#if DEBUG
                        printfn "wsfscservice.exe compiled in %s with error code: %i" location i
#endif
                        waiter.Set()
                        token.Cancel()
                    | x -> 
#if DEBUG
                        eprintfn "Unrecognizable message from server: %s" x
#endif
                        waiter.Set()
                        token.Cancel()
                    async.Zero()
                do! clientPipe.AsyncWrite(bytes, 0, bytes.Length)
                clientPipe.Flush()
                do! readingMessages clientPipe printResponse
                if returnCode = 0 then
#if DEBUG
                    eprintfn "Listening for server finished abruptly"
#endif
                    returnCode <- -12211
                waiter.Set()
                }
            try
                Async.RunSynchronously(write, cancellationToken = token.Token)
            with
            | _ -> 
                waiter.Set()

    let bf = new BinaryFormatter();
    use ms = new MemoryStream()

#if DEBUG
    printfn "WebSharper compilation arguments:"
    args |> Array.iter (printfn "    %s")
#endif
    let startCompileMessage: ArgsType = {args = args}
    bf.Serialize(ms, startCompileMessage);
    ms.Flush();
    let data = ms.ToArray();
    clientPipe.Connect()
    clientPipe.ReadMode <- PipeTransmissionMode.Message
    Write data
    waiter.Wait()
    if isServerNeeded then
        proc.Disposed.RemoveHandler disposedEventHandler 
        proc.Exited.RemoveHandler exitedEventHandler
    returnCode
