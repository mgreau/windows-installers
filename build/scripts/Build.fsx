﻿#I "../../packages/build/FAKE.x64/tools"

#r "FakeLib.dll"

#load "Paths.fsx"
#load "Products.fsx"
#load "Versions.fsx"
#load "BuildConfig.fsx"
#load "Artifacts.fsx"

open System
open System.Diagnostics
open System.Text
open Fake
open Fake.AssemblyInfoFile
open Fake.Git
open Products
open Paths
open Versions
open Artifacts

/// Signs a file using a certificate
let sign file productTitle =
    let release = getBuildParam "release" = "1"
    if release then
        let certificate = getBuildParam "certificate"
        let password = getBuildParam "password"
        let timestampServer = "http://timestamp.comodoca.com"
        let timeout = TimeSpan.FromMinutes 1.

        let sign () =
            let signToolExe = ToolsDir @@ "signtool/signtool.exe"
            let args = ["sign"; "/f"; certificate; "/p"; password; "/t"; timestampServer; "/d"; productTitle; "/v"; file] |> String.concat " "
            let redactedArgs = args.Replace(password, "<redacted>")

            use proc = new Process()
            proc.StartInfo.UseShellExecute <- false
            proc.StartInfo.FileName <- signToolExe
            proc.StartInfo.Arguments <- args
            platformInfoAction proc.StartInfo
            proc.StartInfo.RedirectStandardOutput <- true
            proc.StartInfo.RedirectStandardError <- true
            if isMono then
                proc.StartInfo.StandardOutputEncoding <- Encoding.UTF8
                proc.StartInfo.StandardErrorEncoding  <- Encoding.UTF8
            proc.ErrorDataReceived.Add(fun d -> if d.Data <> null then traceError d.Data)
            proc.OutputDataReceived.Add(fun d -> if d.Data <> null then trace d.Data)

            try
                tracefn "%s %s" proc.StartInfo.FileName redactedArgs
                start proc
            with exn -> failwithf "Start of process %s failed. %s" proc.StartInfo.FileName exn.Message
            proc.BeginErrorReadLine()
            proc.BeginOutputReadLine()
            if not <| proc.WaitForExit(int timeout.TotalMilliseconds) then
                try
                    proc.Kill()
                with exn ->
                    traceError
                    <| sprintf "Could not kill process %s  %s after timeout." proc.StartInfo.FileName redactedArgs
                failwithf "Process %s %s timed out." proc.StartInfo.FileName redactedArgs
            proc.WaitForExit()
            proc.ExitCode

        let exitCode = sign()
        if exitCode <> 0 then failwithf "Signing %s returned error exit code: %i" productTitle exitCode

/// Patches the assembly information for a resolved artifact
let patchAssemblyInformation (resolvedArtifact: ResolvedArtifact) = 
    let version = resolvedArtifact.Version.FullVersion
    let commitHash = Information.getCurrentHash()
    let file = resolvedArtifact.ServiceDir @@ "Properties" @@ "AssemblyInfo.cs"
    CreateCSharpAssemblyInfo file
        [ Attribute.Title resolvedArtifact.Product.AssemblyTitle
          Attribute.Description resolvedArtifact.Product.AssemblyDescription
          Attribute.Guid resolvedArtifact.Product.AssemblyGuid
          Attribute.Product resolvedArtifact.Product.Title
          Attribute.Metadata("GitBuildHash", commitHash)
          Attribute.Company  "Elasticsearch BV"
          Attribute.Copyright "Apache License, version 2 (ALv2). Copyright Elasticsearch."
          Attribute.Trademark (sprintf "%s is a trademark of Elasticsearch BV, registered in the U.S. and in other countries." resolvedArtifact.Product.Title)
          Attribute.Version  version
          Attribute.FileVersion version
          Attribute.InformationalVersion version ] // Attribute.Version and Attribute.FileVersion normalize the version number, so retain the prelease suffix

// Builds a service executable for a resolved artifact
let buildService (resolvedArtifact: ResolvedArtifact) =
    patchAssemblyInformation resolvedArtifact
    
    !! (resolvedArtifact.ServiceDir @@ "*.csproj")
    |> MSBuildRelease resolvedArtifact.ServiceBinDir "Build"
    |> ignore
    
    let serviceAssembly = resolvedArtifact.ServiceBinDir @@ (sprintf "%s.exe" resolvedArtifact.Product.Name)
    let service = resolvedArtifact.BinDir @@ (sprintf "%s.exe" resolvedArtifact.Product.Name)
    CopyFile service serviceAssembly
    sign service resolvedArtifact.Product.Title
    
// Builds an MSI from the files located in a resolved artifact
let buildMsi (resolvedArtifact: ResolvedArtifact) =
    // Compile the MSI project once
    !! (MsiDir @@ "*.csproj")
    |> MSBuildRelease MsiBuildDir "Build"
    |> ignore
    
    if not <| directoryExists resolvedArtifact.OutMsiDir then CreateDir resolvedArtifact.OutMsiDir   
    
    match resolvedArtifact.Distribution with
    | Zip ->
       let exitCode = ExecProcess (fun info ->
                        info.FileName <- sprintf "%sElastic.Installer.Msi" MsiBuildDir
                        info.WorkingDirectory <- MsiDir
                        info.Arguments <- [ resolvedArtifact.Product.Name;
                                            resolvedArtifact.Version.FullVersion;
                                            resolvedArtifact.ExtractedDirectory ]
                                          |> String.concat " "
                       ) <| TimeSpan.FromMinutes 20.
    
       if exitCode <> 0 then failwithf "Error building MSI for %s %s" resolvedArtifact.Product.Name (resolvedArtifact.Version.ToString())            
       CopyFile resolvedArtifact.OutMsiPath (MsiDir @@ (sprintf "%s.msi" resolvedArtifact.Product.Name))
       sign resolvedArtifact.OutMsiPath resolvedArtifact.Product.Title
    | _ ->
       // just copy the distribution
       if not <| fileExists resolvedArtifact.DownloadPath then failwithf "No file found at %s" resolvedArtifact.DownloadPath
       CopyFile resolvedArtifact.OutMsiPath (resolvedArtifact.DownloadPath)
        