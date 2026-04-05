module App

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Amazon.S3
open Anthropic
open Google.GenAI
open Quartz
open ObjectStorage
open AppAnthropic
open AppGeminiCommon
open DomainModel
open Serialisation

let resultMessage (result: Result<'a, 'b>) = if result.IsOk then "Ok" else "Error"

let updateDerivedFeed (storage: ObjectStorageService) (source: SourceSetting) modelActions fetchSource =
    task {
        let! maybeDerivedBatch = DerivedFeeds.submitSummaryBatch storage source fetchSource modelActions

        match maybeDerivedBatch with
        | Some derivedBatch ->
            Console.WriteLine
                $"{source.SourceSlug} request batch update: {derivedBatch.Id}, {derivedBatch.BatchItems.Length} items"

            let! appendBatchResult = DerivedFeeds.appendToFeed storage source derivedBatch
            Console.WriteLine $"{source.SourceSlug} request batch result: {resultMessage appendBatchResult}"
            ()
        | _ -> ()

        let! pollFeedUpdateResult = DerivedFeeds.tryFeedUpdateWithSummaryResults storage source modelActions

        let pollFeedMessage =
            match pollFeedUpdateResult with
            | Ok 0 -> "feed unchanged"
            | Ok value -> $"{value} records added"
            | Error code -> $"failed with status code {code}"

        Console.WriteLine $"{source.SourceSlug} poll derived feed update: {pollFeedMessage}"
        ()
    }

let handleSource (storage: ObjectStorageService) (anthropic: AnthropicService) (gemini: GeminiService) source =
    task {
        let modelActions =
            match source.Model, source.Synchronous with
            | ClaudeHaiku45, _ -> anthropic.Actions
            | Gemini25FlashLite, None -> gemini.Actions
            | Gemini25FlashLite, Some b ->
                if b then gemini.SynchronousActions
                else gemini.Actions

        let updateDerivedFeed' = updateDerivedFeed storage source modelActions

        return!
            match source.SourceSlug with
            | SourceSlug.Artemis -> updateDerivedFeed' SourceFeeds.Artemis.fetchSource
            | GroceryDive -> updateDerivedFeed' SourceFeeds.Dive.fetchSource
            | CStoreDive -> updateDerivedFeed' SourceFeeds.Dive.fetchSource
            | _ -> failwith "not implemented"
    }

let handleSink (storage: ObjectStorageService) (sink: SinkSetting) =
    task {
        let! feedUpdateResult = SinkFeeds.feedUpdate storage sink
        match feedUpdateResult with
        | Ok _ -> ()
        | Error result -> Console.WriteLine $"Sink feed update failed with status code {result}"
    }

type RssSyncJob(
    storage: ObjectStorageService,
    anthropic: AnthropicService,
    gemini: GeminiService,
    sourceSettings: SourceSettings,
    sinkSettings: SinkSettings) =

    interface IJob with
        member _.Execute(context) =
            task {
                Console.WriteLine $"RssSyncJob called: {DateTimeOffset.UtcNow}"
                let enabledSources = sourceSettings.Sources |> Array.filter _.Enabled
                do! Array.map (handleSource storage anthropic gemini) enabledSources |> Task.WhenAll :> Task
                do! Array.map (handleSink storage) sinkSettings.Sinks |> Task.WhenAll :> Task
            }

[<EntryPoint>]
let main args =
    // Load settings using existing JSON deserializer (handles F# types correctly)
    let sourceSettings = File.ReadAllText "source-settings.json" |> deserialise<SourceSettings>
    let sinkSettings = File.ReadAllText "sink-settings.json" |> deserialise<SinkSettings>

    Host.CreateDefaultBuilder(args)
        .ConfigureServices(fun services ->
            // Register settings as singletons
            services.AddSingleton<SourceSettings>(sourceSettings) |> ignore
            services.AddSingleton<SinkSettings>(sinkSettings) |> ignore

            // Register S3 client and ObjectStorageService
            let endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3")
            let bucketName = Environment.GetEnvironmentVariable("TIGRIS_BUCKET")
            let s3Client = new AmazonS3Client(AmazonS3Config(ServiceURL = endpoint, ForcePathStyle = true))
            services.AddSingleton<IAmazonS3>(s3Client) |> ignore
            services.AddSingleton<ObjectStorageService>(fun _ -> ObjectStorageService(s3Client, bucketName)) |> ignore

            // Register LLM services
            services.AddSingleton<AnthropicClient>() |> ignore
            services.AddSingleton<AnthropicService>() |> ignore
            services.AddSingleton<Client>() |> ignore
            services.AddSingleton<GeminiService>() |> ignore

            // Configure Quartz
            services.AddQuartz(fun q ->
                let jobKey = JobKey("rss-sync")
                q.AddJob<RssSyncJob>(jobKey) |> ignore
                q.AddTrigger(fun t ->
                    t.ForJob(jobKey)
                        .WithCronSchedule("0 */3 * * * ?")
                    |> ignore
                ) |> ignore
            ) |> ignore

            services.AddQuartzHostedService(fun opt ->
                opt.WaitForJobsToComplete <- true
            ) |> ignore
        )
        .Build()
        .Run()
    0
