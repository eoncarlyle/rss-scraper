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
        let modelActions =
            firstSource.Model
            |> Option.map (fun m ->
                if m = ClaudeHaiku45 then
                    AppAnthropic.Haiku45Actions
                else
                    failwith "not implemented")
            |> Option.defaultValue AppAnthropic.Haiku45Actions

        if firstSource.SourceSlug = "artemis" then
            let! _ = parseSourceWithSubmitBatch firstSource OriginalSourceFeeds.Artemis.fetchSource modelActions
            let! _ = getFeedUpdate firstSource modelActions
            ()
        else
            failwith "not implemented"
    }
    |> Task.WaitAll

    0
