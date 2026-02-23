module RssScraper.App

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.ViewEngine

open Scrape

type Handler = HttpFunc -> HttpContext -> HttpFuncResult

let slugMap =
    Map
        [ "thediff.rss", TheDiff.getRss
          "gfile.rss", TheDispatch.getRss "gfile"
          "capitolism.rss", TheDispatch.getRss "capitolism"
          "wanderland.rss", TheDispatch.getRss "wanderland" ]

let cache = Caching.getCache ()


let rssHandler getRss =
    fun _ (ctx: HttpContext) ->
        let xml = RenderView.AsString.xmlNode getRss
        ctx.SetContentType "application/rss+xml; charset=utf-8"
        ctx.WriteStringAsync xml

let rssSlugHandler slug : Handler =
    Map.tryFind slug slugMap
    |> Option.map (fun getRss ->
        Caching.getOrElseWith slug getRss cache |> rssHandler
        >=> publicResponseCaching 60 None)
    |> Option.defaultValue (setStatusCode 404 >=> text "Not Found")

let webApp =
    choose
        [ GET >=> choose [ route "/" >=> text "up"; routef "/%s" rssSlugHandler ]
          setStatusCode 404 >=> text "Not Found" ]


let errorHandler (ex: Exception) (logger: ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

let configureApp (app: IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()

    (match env.IsDevelopment() with
     | true -> app.UseDeveloperExceptionPage()
     | false -> app.UseGiraffeErrorHandler(errorHandler).UseHttpsRedirection())
        //.UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services: IServiceCollection) =
    //services.AddCors()    |> ignore
    services.AddGiraffe() |> ignore

let configureLogging (builder: ILoggingBuilder) =
    builder.AddConsole().AddDebug() |> ignore

type AppArgs = { HostAddress: string }

let getAppArgs args =
    let argList = Array.toList args

    match argList with
    | [ hostAddress ] -> Some { HostAddress = hostAddress }
    | _ -> None

[<EntryPoint>]
let main args =
    let appArgs = getAppArgs args |> Option.get
    let port = 5050

    Host
        .CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(fun webHostBuilder ->
            webHostBuilder
                .UseUrls($"http://{appArgs.HostAddress}:{port}")
                //.UseContentRoot(contentRoot)
                //.UseWebRoot(webRoot)
                .Configure(Action<IApplicationBuilder> configureApp)
                .ConfigureServices(configureServices)
                .ConfigureLogging(configureLogging)
            |> ignore)
        .Build()
        .Run()

    0
