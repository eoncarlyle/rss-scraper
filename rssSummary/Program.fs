module RssSummary.App

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Amazon.S3
open Anthropic
open Quartz
open Serilog
open ObjectStorage
open AppAnthropic
open AppGeminiCommon
open DomainModel
open Serialisation

type MsLogger = Microsoft.Extensions.Logging.ILogger
type MsLogger<'T> = Microsoft.Extensions.Logging.ILogger<'T>

type AppExecution =
    | Persistent
    | AdHoc

let getAppMode args =
    match Array.toList args with
    | [ "--ad-hoc" ] -> AdHoc
    | _ -> Persistent

let resultMessage (result: Result<'a, 'b>) = if result.IsOk then "Ok" else "Error"

let updateDerivedFeed
    (logger: MsLogger)
    (storage: ObjectStorageService)
    (source: SourceSetting)
    modelActions
    fetchSource
    =
    task {
        let! maybeDerivedBatch = DerivedFeeds.submitSummaryBatch storage source fetchSource modelActions

        match maybeDerivedBatch with
        | Some derivedBatch ->
            logger.LogInformation(
                "{SourceSlug} request batch update: {BatchId}, {ItemCount} items",
                source.SourceSlug,
                derivedBatch.Id,
                derivedBatch.BatchItems.Length
            )

            let! appendBatchResult = DerivedFeeds.appendToFeed storage source derivedBatch

            logger.LogInformation(
                "{SourceSlug} request batch result: {Result}",
                source.SourceSlug,
                resultMessage appendBatchResult
            )

            ()
        | _ -> ()

        let! pollFeedUpdateResult = DerivedFeeds.tryFeedUpdateWithSummaryResults storage source modelActions

        let pollFeedMessage =
            match pollFeedUpdateResult with
            | Ok 0 -> "feed unchanged"
            | Ok value -> $"{value} records added"
            | Error code -> $"failed with status code {code}"

        logger.LogInformation("{SourceSlug} poll derived feed update: {Message}", source.SourceSlug, pollFeedMessage)
        ()
    }

let handleSource
    (logger: MsLogger)
    (storage: ObjectStorageService)
    (anthropic: AnthropicService)
    (gemini: GeminiService)
    source
    =
    task {
        let modelActions =
            match source.Model with
            | ClaudeHaiku45 -> anthropic.Actions source.Synchronous
            | Gemini25FlashLite
            | Gemini31FlashLitePreview -> gemini.Actions source.Synchronous <| Some source.Model

        let updateDerivedFeed' = updateDerivedFeed logger storage source modelActions

        return!
            match source.SourceSlug with
            | SourceSlug.Artemis -> updateDerivedFeed' (SourceFeeds.Artemis.fetchSource logger)
            | GroceryDive -> updateDerivedFeed' (SourceFeeds.Dive.fetchSource logger)
            | CStoreDive -> updateDerivedFeed' (SourceFeeds.Dive.fetchSource logger)
            | SupplyChainDive -> updateDerivedFeed' (SourceFeeds.Dive.fetchSource logger)
            | TheZvi -> updateDerivedFeed' (SourceFeeds.Substack.fetchSource logger)
            | Noahpinion -> updateDerivedFeed' (SourceFeeds.Substack.fetchSource logger)
            | ChinaTalk -> updateDerivedFeed' (SourceFeeds.Substack.fetchSource logger)
            | SourceSlug.MarginalRevolution -> updateDerivedFeed' (SourceFeeds.MarginalRevolution.fetchSource logger)
    }

let validateUnique items (getSlug: 'a -> 'slug) label =
    let duplicates =
        items
        |> Seq.groupBy getSlug
        |> Seq.filter (fun (_, group) -> Seq.length group > 1)
        |> Seq.map (fun (slug, _) -> slug.ToString())
        |> Seq.toList

    if not (List.isEmpty duplicates) then
        failwithf "Duplicate %s slugs found: %A" label duplicates

let handleSink (logger: MsLogger) (storage: ObjectStorageService) (sink: SinkSetting) =
    task {
        let! feedUpdateResult = SinkFeeds.feedUpdate logger storage sink

        match feedUpdateResult with
        | Ok _ -> ()
        | Error result -> logger.LogWarning("Sink feed update failed with status code {StatusCode}", result)
    }

type RssSyncJob
    (
        logger: MsLogger<RssSyncJob>,
        storage: ObjectStorageService,
        anthropic: AnthropicService,
        gemini: GeminiService,
        sourceSettings: SourceSettings,
        sinkSettings: SinkSettings
    ) =

    interface IJob with
        member _.Execute context =
            task {
                logger.LogInformation("RssSyncJob called: {Timestamp}", DateTimeOffset.UtcNow)
                let enabledSources = sourceSettings.Sources |> Array.filter _.Enabled

                do!
                    Array.map (handleSource logger storage anthropic gemini) enabledSources
                    |> Task.WhenAll
                    :> Task

                do! Array.map (handleSink logger storage) sinkSettings.Sinks |> Task.WhenAll :> Task
            }

let configureServices (sourceSettings: SourceSettings) (sinkSettings: SinkSettings) (services: IServiceCollection) =
    services.AddSingleton<SourceSettings> sourceSettings |> ignore
    services.AddSingleton<SinkSettings> sinkSettings |> ignore

    let endpoint = Environment.GetEnvironmentVariable "AWS_ENDPOINT_URL_S3"
    let bucketName = Environment.GetEnvironmentVariable "TIGRIS_BUCKET"

    let s3Client =
        new AmazonS3Client(AmazonS3Config(ServiceURL = endpoint, ForcePathStyle = true))

    services.AddSingleton<IAmazonS3> s3Client |> ignore

    services.AddSingleton<ObjectStorageService>(fun _ -> ObjectStorageService(s3Client, bucketName))
    |> ignore

    services.AddSingleton<AnthropicClient>() |> ignore
    services.AddSingleton<AnthropicService>() |> ignore
    services.AddSingleton<GeminiClient>() |> ignore
    services.AddSingleton<GeminiService>() |> ignore
    services.AddLogging(fun b -> b.AddSerilog() |> ignore) |> ignore

let runAdHoc (sourceSettings: SourceSettings) (sinkSettings: SinkSettings) =
    task {
        let services = ServiceCollection()
        configureServices sourceSettings sinkSettings services
        let provider = services.BuildServiceProvider()

        let logger = provider.GetRequiredService<MsLogger<RssSyncJob>>()
        let storage = provider.GetRequiredService<ObjectStorageService>()
        let anthropic = provider.GetRequiredService<AnthropicService>()
        let gemini = provider.GetRequiredService<GeminiService>()

        let job = RssSyncJob(logger, storage, anthropic, gemini, sourceSettings, sinkSettings)
        do! (job :> IJob).Execute null
    }

let runPersistent args (sourceSettings: SourceSettings) (sinkSettings: SinkSettings) =
    Host
        .CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices(fun services ->
            configureServices sourceSettings sinkSettings services

            services.AddQuartz(fun q ->
                let jobKey = JobKey "rss-sync"
                q.AddJob<RssSyncJob> jobKey |> ignore

                q.AddTrigger(fun t -> t.ForJob(jobKey).WithCronSchedule "0 */10 * * * ?" |> ignore)
                |> ignore)
            |> ignore

            services.AddQuartzHostedService(fun opt -> opt.WaitForJobsToComplete <- true)
            |> ignore)
        .Build()
        .Run()

[<EntryPoint>]
let main args =
    Log.Logger <-
        LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger()

    let sourceSettings =
        File.ReadAllText "source-settings.json" |> deserialise<SourceSettings>

    validateUnique sourceSettings.Sources _.SourceSlug "source"

    let sinkSettings =
        File.ReadAllText "sink-settings.json" |> deserialise<SinkSettings>

    validateUnique sinkSettings.Sinks _.SinkSlug "sink"

    match getAppMode args with
    | AdHoc ->
        Log.Information "Running ad-hoc"
        (runAdHoc sourceSettings sinkSettings).Wait()
        0
    | Persistent ->
        runPersistent args sourceSettings sinkSettings
        0
