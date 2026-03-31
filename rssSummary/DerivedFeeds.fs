module DerivedFeeds

open System.IO
open System
open System.Threading.Tasks
open Queries
open Serialisation
open DomainModels


let sourcesConfiguration =
    File.ReadAllText "sourceFeeds.json" |> deserializeSourcesConfiguration

let getDerivedSourceFeedKey (sourceFeed: SourceFeed) = $"{sourceFeed.SourceSlug}.derived.json"

let isEquivalent (item: RssItem) (batchItem: DerivedItem) =
    item.Title = batchItem.Item.Title
    && item.Guid = batchItem.Item.Guid
    && item.Link = batchItem.Item.Link

// Need to accept a factory to get the full item for a fresh etag: not the first time I've made this mistake
//let putWithRetry (times: int) (feedKey: string) (factory: string -> Task<Result<string, Net.HttpStatusCode>>) =
//    let mutable result = Error (Net.HttpStatusCode.BadRequest)
//    task { 
//        for _ in 1 .. times do
//           if result.IsError then
//                let! maybeS3Object = ObjectStorage.getS3Object feedKey
//                let! nextResult = factory ()
//                result <- nextResult
//        return result
//    }    
    //{ 1..times }
    //|> Seq.map (fun _ -> factory)
    //|> Seq.reduce (fun f1 f2 ->
    //    task
    //        {
    //            return ()
    //        } |> Task.WaitAny)



let getSourceItemsToSubmit (sourceFeed: SourceFeed) (incomingRssItems: RssItem array) =
    task {
        let feedKey = getDerivedSourceFeedKey sourceFeed
        let! maybeS3Object = ObjectStorage.getS3Object feedKey

        match maybeS3Object with
        | Some s3Object ->

            let existingDerivedFeed = deserializeDerivedSourceFeed s3Object.Content

            let submittedBatchItems =
                existingDerivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

            if Array.length submittedBatchItems = 0 then
                return Array.truncate sourceFeed.MaximumLookback incomingRssItems
            else
                let truncationIndex =
                    incomingRssItems
                    |> Array.tryFindIndexBack (fun incomingItem ->
                        submittedBatchItems
                        |> Array.tryFind (fun batchItem -> isEquivalent incomingItem batchItem)
                        |> Option.isSome)
                    |> Option.defaultValue sourceFeed.MaximumLookback

                return
                    incomingRssItems
                    |> Array.truncate truncationIndex
                    |> Array.filter (fun sourceItem ->
                        let maybeMatch =
                            submittedBatchItems
                            |> Array.tryFind (fun batchItem -> isEquivalent sourceItem batchItem)

                        maybeMatch.IsNone)
        | None -> return Array.truncate sourceFeed.MaximumLookback incomingRssItems
    }

let parseSourceWithSubmitBatch
    source
    (fetchSource: string -> Task<RssItem array>)
    (modelActions: LangaugeModelActions)
    =
    task {
        let! incomingSourceItems = fetchSource source.SourceUrl
        let! submittedSourceItems = getSourceItemsToSubmit source incomingSourceItems

        if Array.length submittedSourceItems > 0 then
            let submitBatchParameters =
                { SystemPrompt = Option.defaultValue defaultSystemPrompt source.SystemPrompt
                  InputTokenCutoff = source.InputTokenCutoff
                  OutputTokenCutoff = source.OutputTokenCutoff }

            let! requestBatch = modelActions.SubmitBatch submittedSourceItems submitBatchParameters

            return Some requestBatch
        else
            return None
    }

let writeUpdatedDerivedSourceFeed (source: SourceFeed) (updatedDerivedFeed: DerivedFeed) =
    task {
        let feedKey = getDerivedSourceFeedKey source
        let! maybeS3Object = ObjectStorage.getS3Object feedKey

        if not maybeS3Object.IsSome then
            failwith $"Derived feed {feedKey} does not exist"

        return!
            ObjectStorage.putS3Object
                feedKey
                (serializeDerivedSourceFeed updatedDerivedFeed)
                (maybeS3Object |> Option.map _.ETag)
    }

let appendBatchToFeed (sourceFeed: SourceFeed) (derivedBatch: DerivedBatch) =
    task {
        let feedKey = getDerivedSourceFeedKey sourceFeed
        let! maybeS3Object = ObjectStorage.getS3Object feedKey

        match maybeS3Object with
        | Some s3Object ->
            let existingDerivedFeed = deserializeDerivedSourceFeed s3Object.Content

            let updatedDerivedFeed =
                { SourceUrl = existingDerivedFeed.SourceUrl
                  Batches = Array.append existingDerivedFeed.Batches [| derivedBatch |] }

            return!
                ObjectStorage.putS3Object feedKey (serializeDerivedSourceFeed updatedDerivedFeed) (Some s3Object.ETag)
        | None ->
            let derivedSourceFeed =
                { SourceUrl = sourceFeed.SourceUrl
                  Batches = [| derivedBatch |] }

            return! ObjectStorage.putS3Object feedKey (serializeDerivedSourceFeed derivedSourceFeed) None
    }

let pollSource source fetchSource modelActions =
    task {
        let! maybeSubmitBatch = parseSourceWithSubmitBatch source fetchSource modelActions

        match maybeSubmitBatch with
        | Some batch ->
            let! b = appendBatchToFeed source batch
            return Some b
        | None -> return None
    }

let tryPollFeedUpdate sourceFeed modelActions =
    task {
        let feedKey = getDerivedSourceFeedKey sourceFeed
        let! maybeS3Object = ObjectStorage.getS3Object feedKey

        match maybeS3Object with
        | Some s3Object ->
            let derivedSourceFeed = deserializeDerivedSourceFeed s3Object.Content
            let! maybeUpdatedDerivedSourceFeed = modelActions.GetUpdatedDerivedFeed sourceFeed derivedSourceFeed

            match maybeUpdatedDerivedSourceFeed with
            | Some updated ->
                let! putResult =
                    ObjectStorage.putS3Object feedKey (serializeDerivedSourceFeed updated) (Some s3Object.ETag)

                return putResult |> Result.map (fun _ -> true)
            | None -> return Ok false
        | None -> return Error Net.HttpStatusCode.NotFound
    }
