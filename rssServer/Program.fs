module RssScraper.App

open System
open Caching
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Http
open System.IO
open Giraffe
open Giraffe.ViewEngine
open Amazon.S3
open ObjectStorage
open Scrape
open Serialisation
open Serilog

type MsLogger = Microsoft.Extensions.Logging.ILogger
type MsLogger<'T> = Microsoft.Extensions.Logging.ILogger<'T>

let directScrapeHandler slug getRss =
    fun _ (ctx: HttpContext) ->
        let cache = ctx.GetService<Cache>()
        let xml = cache.getOrElseWithDirect (slug, getRss)
        let rendered = RenderView.AsString.xmlNode xml
        ctx.SetContentType "application/rss+xml; charset=utf-8"
        ctx.WriteStringAsync rendered

let summaryHandler slug : HttpHandler =
    fun _ (ctx: HttpContext) ->
        task {
            let cache = ctx.GetService<Cache>()
            let objectStorageService = ctx.GetService<ObjectStorageService>()

            let getSummaryThunk =
                fun () ->
                    task {
                        let possibleFeedKey = SinkFeeds.getPossibleFeedKey slug
                        let! maybeSinkS3Object = objectStorageService.GetObject possibleFeedKey
                        Log.Information("Summary feed cache miss, attempting to retrieve {FeedKey}", possibleFeedKey)

                        return
                            maybeSinkS3Object
                            |> Option.map (fun b -> deserialise<DomainModel.SinkFeed> b.Content)
                            |> Option.map RenderSummarised.getRss
                    }

            let! maybeXml = cache.getOrElseWithSummary (slug, getSummaryThunk)

            return!
                match maybeXml with
                | Some xml ->
                    let rendered = RenderView.AsString.xmlNode xml
                    Log.Information("Returning output for summary feed {Slug}", slug)
                    ctx.SetContentType "application/rss+xml; charset=utf-8"
                    ctx.WriteStringAsync rendered
                | None ->
                    Log.Warning("No output for summary feed {Slug}", slug)
                    ctx.SetStatusCode 404
                    ctx.SetContentType "text/plain; charset=utf-8"
                    ctx.WriteStringAsync "Not Found"
        }

let slugRouter feed : HttpHandler =
    match feed with
    | "thediff.rss" -> directScrapeHandler feed TheDiff.getRss >=> publicResponseCaching 60 None
    | p when p.EndsWith(".rss", StringComparison.OrdinalIgnoreCase) ->
        summaryHandler (Path.GetFileNameWithoutExtension(p))
        >=> publicResponseCaching 60 None
    | _ -> setStatusCode 404 >=> text "Not Found"

let webApp =
    choose
        [ GET >=> choose [ routef "/%s" slugRouter ]
          HEAD >=> setStatusCode 200
          setStatusCode 404 >=> text "Not Found" ]

let errorHandler (ex: Exception) (logger: MsLogger) =
    Log.Error(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

let configureApp (app: IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()

    (match env.IsDevelopment() with
     | true -> app.UseDeveloperExceptionPage()
     | false -> app.UseGiraffeErrorHandler(errorHandler).UseHttpsRedirection())
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services: IServiceCollection) =
    services.AddGiraffe() |> ignore
    let endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3")
    let bucketName = Environment.GetEnvironmentVariable("TIGRIS_BUCKET")

    let s3Client =
        new AmazonS3Client(AmazonS3Config(ServiceURL = endpoint, ForcePathStyle = true))

    services.AddSingleton<IAmazonS3>(s3Client) |> ignore

    services.AddSingleton<ObjectStorageService>(ObjectStorageService(s3Client, bucketName))
    |> ignore

    services.AddSingleton<Cache>(Cache()) |> ignore


let configureLogging (builder: ILoggingBuilder) =
    builder.ClearProviders() |> ignore

type AppArgs = { HostAddress: string }

let getAppArgs args =
    let argList = Array.toList args

    match argList with
    | [ hostAddress ] -> Some { HostAddress = hostAddress }
    | _ -> None

[<EntryPoint>]
let main args =
    Log.Logger <-
        LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger()

    let appArgs = getAppArgs args |> Option.get
    let port = 5050

    Host
        .CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureWebHostDefaults(fun webHostBuilder ->
            webHostBuilder
                .UseUrls($"http://{appArgs.HostAddress}:{port}")
                .Configure(Action<IApplicationBuilder> configureApp)
                .ConfigureServices(configureServices)
                .ConfigureLogging(configureLogging)
            |> ignore)
        .Build()
        .Run()

    0
