module DerivedFeeds

open System.IO
open System
open System.Threading.Tasks
open Serialisation
open DomainModel
open LanguageModelCommon

let sourcesConfiguration =
    File.ReadAllText "sourceFeeds.json" |> deserialiseSourceSettings

let getDerivedFeedKey (sourceSetting: SourceSetting) = $"{sourceSetting.SourceSlug}.derived.json"

let isEquivalent (item: RssItem) (batchItem: DerivedItem) =
    item.Title = batchItem.Item.Title
    && item.Guid = batchItem.Item.Guid
    && item.Link = batchItem.Item.Link

// Need to accept a factory to get the full item for a fresh etag: not the first time I've made this mistake
let retryHttp (times: int) (factory: Unit -> Task<Result<'a, Net.HttpStatusCode>>) =
    let mutable result = Error(Net.HttpStatusCode.BadRequest)

    task {
        for _ in 1..times do
            if result.IsError then
                let! nextResult = factory ()
                result <- nextResult

        return result
    }
//{ 1..times }
//|> Seq.map (fun _ -> factory)
//|> Seq.reduce (fun f1 f2 ->
//    task
//        {
//            return ()
//        } |> Task.WaitAny)



let getSourceItemsToSubmit (sourceSetting: SourceSetting) (incomingRssItems: RssItem array) =
    task {
        let feedKey = getDerivedFeedKey sourceSetting
        let! maybeS3Object = ObjectStorage.getS3Object feedKey

        match maybeS3Object with
        | Some s3Object ->

            let existingDerivedFeed = deserialiseDerivedFeed s3Object.Content

            let submittedBatchItems =
                existingDerivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

            if Array.length submittedBatchItems = 0 then
                return Array.truncate sourceSetting.MaximumLookback incomingRssItems
            else
                let truncationIndex =
                    incomingRssItems
                    |> Array.tryFindIndexBack (fun incomingItem ->
                        submittedBatchItems
                        |> Array.tryFind (fun batchItem -> isEquivalent incomingItem batchItem)
                        |> Option.isSome)
                    |> Option.defaultValue sourceSetting.MaximumLookback

                return
                    incomingRssItems
                    |> Array.truncate truncationIndex
                    |> Array.filter (fun sourceItem ->
                        let maybeMatch =
                            submittedBatchItems
                            |> Array.tryFind (fun batchItem -> isEquivalent sourceItem batchItem)

                        maybeMatch.IsNone)
        | None -> return Array.truncate sourceSetting.MaximumLookback incomingRssItems
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

// Possibly not used?
let writeUpdatedDerivedSourceFeed (sourceSetting: SourceSetting) (updatedDerivedFeed: DerivedFeed) =
    //task {
    //    let feedKey = getDerivedSourceFeedKey sourceFeed
    //    let! maybeS3Object = ObjectStorage.getS3Object feedKey

    //    if not maybeS3Object.IsSome then
    //        failwith $"Derived feed {feedKey} does not exist"

    //    return!
    //        ObjectStorage.putS3Object
    //            feedKey
    //            (serializeDerivedSourceFeed updatedDerivedFeed)
    //            (maybeS3Object |> Option.map _.ETag)
    //}
    ()

let appendBatchToFeed (sourceSetting: SourceSetting) (derivedBatch: DerivedBatch) =
    let append sourceSetting derivedBatch =
        task {
            let feedKey = getDerivedFeedKey sourceSetting
            let! maybeS3Object = ObjectStorage.getS3Object feedKey

            match maybeS3Object with
            | Some s3Object ->
                let existingDerivedFeed = deserialiseDerivedFeed s3Object.Content

                let updatedDerivedFeed =
                    { SourceUrl = existingDerivedFeed.SourceUrl
                      Batches = Array.append existingDerivedFeed.Batches [| derivedBatch |] }

                return!
                    ObjectStorage.putS3Object
                        feedKey
                        (serialiseDerivedFeed updatedDerivedFeed)
                        (Some s3Object.ETag)
            | None ->
                let derivedFeed =
                    { SourceUrl = sourceSetting.SourceUrl
                      Batches = [| derivedBatch |] }

                return! ObjectStorage.putS3Object feedKey (serialiseDerivedFeed derivedFeed) None
        }

    retryHttp 3 (fun () -> append sourceSetting derivedBatch)

let pollSource source fetchSource modelActions =
    task {
        let! maybeSubmitBatch = parseSourceWithSubmitBatch source fetchSource modelActions

        match maybeSubmitBatch with
        | Some batch ->
            let! b = appendBatchToFeed source batch
            return Some b
        | None -> return None
    }

let tryPollFeedUpdate sourceSetting modelActions =
    let update sourceSetting modelActions =
        task {
            let feedKey = getDerivedFeedKey sourceSetting
            let! maybeS3Object = ObjectStorage.getS3Object feedKey

            match maybeS3Object with
            | Some s3Object ->
                let existingDerivedFeed = deserialiseDerivedFeed s3Object.Content
                let! maybeUpdatedDerivedFeed = modelActions.GetUpdatedDerivedFeed sourceSetting existingDerivedFeed

                match maybeUpdatedDerivedFeed with
                | Some updated ->
                    let! putResult =
                        ObjectStorage.putS3Object feedKey (serialiseDerivedFeed updated) (Some s3Object.ETag)

                    let getUpdatedCount batches =
                        batches
                        |> Array.map _.BatchItems
                        |> Array.concat
                        |> Array.filter _.Result.IsSome
                        |> Array.length

                    return
                        putResult
                        |> Result.map (fun _ ->
                            getUpdatedCount updated.Batches - getUpdatedCount existingDerivedFeed.Batches)

                | None -> return Ok 0
            | None -> return Error Net.HttpStatusCode.NotFound
        }

    retryHttp 3 (fun () -> update sourceSetting modelActions)
