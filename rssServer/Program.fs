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
open Amazon.S3
open ObjectStorage

open Scrape

type Handler = HttpFunc -> HttpContext -> HttpFuncResult

let slugMap =
    Map
        [ "thediff.rss", TheDiff.getRss ]

let cache = Caching.getCache ()

let directScrapeHandler getRss =
    fun _ (ctx: HttpContext) ->
        let xml = RenderView.AsString.xmlNode getRss
        ctx.SetContentType "application/rss+xml; charset=utf-8"
        ctx.WriteStringAsync xml

// TODO: 1) Wire in object storage 2) Place cache as service 3) RSS parsing 4) summary slug handling (caching?) 

let slugRouter slug : Handler =
    match slug with
    | "thediff.rss" -> Caching.getOrElseWith slug TheDiff.getRss cache |> directScrapeHandler >=> publicResponseCaching 60 None
    | _ -> setStatusCode 404 >=> text "Not Found"

let webApp =
    choose
        [ GET >=> choose [ routef "/%s" slugRouter ]
          HEAD >=> setStatusCode 200
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
    services.AddGiraffe() |> ignore
    let endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3")
    let bucketName = Environment.GetEnvironmentVariable("TIGRIS_BUCKET")
    let s3Client = new AmazonS3Client(AmazonS3Config(ServiceURL = endpoint, ForcePathStyle = true))
    //services.AddSingleton<IAmazonS3>(s3Client) |> ignore
    //services.AddSingleton<ObjectStorageService>(fun _ -> ObjectStorageService(s3Client, bucketName)) |> ignore


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
