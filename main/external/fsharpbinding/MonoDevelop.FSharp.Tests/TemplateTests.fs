﻿namespace MonoDevelopTests
open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Microsoft.FSharp.Compiler.SourceCodeServices
open Mono.Addins
open MonoDevelop.Core
open MonoDevelop.Core.Instrumentation
open MonoDevelop.Core.ProgressMonitoring
open MonoDevelop.FSharp
open MonoDevelop.Ide
open MonoDevelop.Ide.Projects
open MonoDevelop.Ide.Templates
open MonoDevelop.PackageManagement.Tests.Helpers
open MonoDevelop.Projects
open NUnit.Framework

[<TestFixture>]
type ``Template tests``() =
    let toTask computation : Task = Async.StartAsTask computation :> _

    let monitor = new ConsoleProgressMonitor()
    do
        FixtureSetup.initialiseMonoDevelop()
        let getField name =
            typeof<IdeApp>.GetField(name, BindingFlags.NonPublic ||| BindingFlags.Static)

        let workspace = getField "workspace"
        workspace.SetValue(null, new RootWorkspace())
        let workbench = getField "workbench"
        workbench.SetValue(null, new MonoDevelop.Ide.Gui.Workbench())
        IdeApp.Preferences.MSBuildVerbosity.Value <- MonoDevelop.Projects.MSBuild.MSBuildVerbosity.Minimal

    let templateService = TemplatingService()
    let templateMatch (template:SolutionTemplate) = 
        template.IsMatch (SolutionTemplateVisibility.All)

    let predicate = new Predicate<SolutionTemplate>(templateMatch)

    let rec flattenCategories (category: TemplateCategory) = 
        seq {
            yield category
            yield! category.Categories |> Seq.collect flattenCategories
        }

    let solutionTemplates =
        templateService.GetProjectTemplateCategories (predicate)
        |> Seq.collect flattenCategories
        |> Seq.collect(fun c -> c.Templates)
        |> Seq.choose(fun s -> s.GetTemplate("F#") |> Option.ofObj)
        |> Seq.filter(fun t -> t.Id.IndexOf("SharedAssets") = -1) // shared assets projects can't be built standalone
        |> List.ofSeq

    let templatesDir = FilePath(".").FullPath.ToString() / "buildtemplates"

    [<TestFixtureSetUp>]
    member x.Setup() =
        let config = """
<configuration>  
  <config>
    <add key="repositoryPath" value="packages" />
  </config>
</configuration>"""
        if not (Directory.Exists templatesDir) then
            Directory.CreateDirectory templatesDir |> ignore
        let configFileName = templatesDir/"NuGet.Config"
        File.WriteAllText (configFileName, config, Text.Encoding.UTF8)

    member x.Templates =
        solutionTemplates |> Seq.map (fun t -> t.Id)

    [<Test;AsyncStateMachine(typeof<Task>)>]
    [<TestCaseSource ("Templates")>]
    member x.``Build every template`` (tt:string) =
        if not MonoDevelop.Core.Platform.IsMac then
            Assert.Ignore ()

        if tt = "FSharpPortableLibrary" then
            Assert.Ignore ("A platform service implementation has not been found")
        toTask <| async {
            let projectTemplate = ProjectTemplate.ProjectTemplates |> Seq.find (fun t -> t.Id == tt)
            let dir = FilePath (templatesDir/projectTemplate.Id)
            dir.Delete()
            Directory.CreateDirectory (dir |> string) |> ignore
            let cinfo = new ProjectCreateInformation (ProjectBasePath = dir, ProjectName = tt, SolutionName = tt, SolutionPath = dir)
            cinfo.Parameters.["CreateSharedAssetsProject"] <- "False"
            cinfo.Parameters.["CreatePortableDotNetProject"] <- "True"
            cinfo.Parameters.["CreateMonoTouchProject"] <- "True"
            cinfo.Parameters.["UseXamarinAndroidSupportv7AppCompat"] <- "True"
            cinfo.Parameters.["CreateAndroidProject"] <- "True"
            cinfo.Parameters.["UseUniversal"] <- "True"
            cinfo.Parameters.["UseIPad"] <- "False"
            cinfo.Parameters.["UseIPhone"] <- "False"
            cinfo.Parameters.["CreateiOSUITest"] <- "False"
            cinfo.Parameters.["CreateAndroidUITest"] <- "False"
            cinfo.Parameters.["MinimumOSVersion"] <- "10.0"
            cinfo.Parameters.["AppIdentifier"] <- tt
            cinfo.Parameters.["AndroidMinSdkVersionAttribute"] <- "android:minSdkVersion=\"10\""
            cinfo.Parameters.["AndroidThemeAttribute"] <- ""
            cinfo.Parameters.["TargetFrameworkVersion"] <- "MonoAndroid,Version=v7.0"
            let sln = projectTemplate.CreateWorkspaceItem (cinfo) :?> Solution

            let createTemplate (template:SolutionTemplate) =
                let config = NewProjectConfiguration(
                                CreateSolution = false,
                                ProjectName = tt,
                                SolutionName = tt,
                                Location = (dir |> string)
                             )
                
                templateService.ProcessTemplate(template, config, sln.RootFolder)

            let folder = new SolutionFolder()
            let solutionTemplate =
                solutionTemplates 
                |> Seq.find(fun t -> t.Id = tt)

            let projects = sln.Items |> Seq.filter(fun i -> i :? DotNetProject) |> Seq.cast<DotNetProject> |> List.ofSeq


            do! NuGetPackageInstaller.InstallPackages (sln, projectTemplate.PackageReferencesForCreatedProjects) |> Async.AwaitTask
            do! sln.SaveAsync(monitor) |> Async.AwaitTask
            let getErrorsForProject (projects: DotNetProject list) =
                asyncSeq {
                    let! result = sln.Build(monitor, sln.DefaultConfigurationSelector, null) |> Async.AwaitTask

                    match tt, result.HasWarnings, result.HasErrors with
                    | "Xamarin.tvOS.FSharp.SingleViewApp", _, false //MTOUCH : warning MT0094: Both profiling (--profiling) and incremental builds (--fastdev) is not supported when building for tvOS. Incremental builds have ben disabled.]
                    | _, false, false ->
                        // xbuild worked, now check for editor squiggles
                        for project in projects do
                            let checker = FSharpChecker.Create()
                            let projectOptions = languageService.GetProjectOptionsFromProjectFile project
                            let! checkResult = checker.ParseAndCheckProject projectOptions
                            for error in checkResult.Errors do
                                yield "Editor error", error.FileName, error.Message
                    | _ ->
                        for error in result.Errors do
                            if not error.IsWarning then
                                yield "Build error", error.FileName, error.ErrorText
                }

            let errors = getErrorsForProject projects |> AsyncSeq.toSeq |> List.ofSeq
            match errors with
            | [] -> Assert.Pass()
            | errors -> Assert.Fail (sprintf "%A" errors)
        }