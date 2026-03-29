open System
open System.Threading.Tasks
open DerivedSourceFeeds
open DomainModels
open Giraffe.ViewEngine
open Queries

[<EntryPoint>]
let main args =

    let firstSource = Array.get sourcesConfiguration.Sources 0

    task {
        if firstSource.SourceSlug = "artemis" then
            let modelActions =
                firstSource.Model
                |> Option.map (fun m ->
                    if m = ClaudeHaiku45 then
                        AppAnthropic.Haiku45Actions
                    else
                        failwith "not implemented")
                |> Option.defaultValue AppAnthropic.Haiku45Actions

            let! a = parseSourceWithSubmitBatch firstSource OriginalSourceFeeds.Artemis.fetchSource modelActions
            ()
        else
            failwith "not implemented"

        let! incomingRssItems = OriginalSourceFeeds.Artemis.fetchSource firstSource.SourceUrl
        let! requestBatch = Anthropic.submitStandardBatch incomingRssItems systemPrompt

        let! a = getFeedUpdate firstSource


        Console.WriteLine()
        // TODO: assessing if request *should* be made
        //updateDerivedSourceFeed firstSource requestBatch
        ()
    //Console.WriteLine rssItems
    }
    |> Task.WaitAll

    0
