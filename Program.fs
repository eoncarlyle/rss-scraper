module DiffParse.App

open System
open System.IO
open Microsoft.AspNetCore.Builder
// open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.ViewEngine

open Scrape

type Handler = HttpFunc -> HttpContext -> HttpFuncResult

let scrapePath = "https://thediff.co/archive"

let rssHandler =
    let posts = scrape scrapePath |> Seq.map (rssItem scrapePath) |> Seq.toList
    let rss = rssChannelView "The Diff" "https://thediff.co" "Very simple scraper to get this to show up in my RSS feed" posts
    fun _ (ctx: HttpContext) ->
        let xml = RenderView.AsString.xmlNode rss
        ctx.SetContentType "application/rss+xml; charset=utf-8"
        ctx.WriteStringAsync xml

let webApp =
    choose [
        GET >=>
            choose [
                route "/" >=> rssHandler
           ]
        setStatusCode 404 >=> text "Not Found" ]


let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

let configureApp (app : IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
    (match env.IsDevelopment() with
    | true  ->
        app.UseDeveloperExceptionPage()
    | false ->
        app .UseGiraffeErrorHandler(errorHandler)
            .UseHttpsRedirection())
        //.UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services : IServiceCollection) =
    //services.AddCors()    |> ignore
    services.AddGiraffe() |> ignore

let configureLogging (builder : ILoggingBuilder) =
    builder.AddConsole()
           .AddDebug() |> ignore

[<EntryPoint>]
let main args =
    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot     = Path.Combine(contentRoot, "WebRoot")
    let port = 5050
    Host.CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(
            fun webHostBuilder ->
                webHostBuilder
                    .UseUrls($"http://127.0.0.1:{port}")
                    .UseContentRoot(contentRoot)
                    .UseWebRoot(webRoot)
                    .Configure(Action<IApplicationBuilder> configureApp)
                    .ConfigureServices(configureServices)
                    .ConfigureLogging(configureLogging)
                    |> ignore)
        .Build()
        .Run()
    0
