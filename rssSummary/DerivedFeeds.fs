module DerivedFeeds

open System
open System.Threading.Tasks
open FIO.DSL
open Serialisation
open DomainModel
open LanguageModelCommon
open ObjectStorage

let getDerivedFeedKeyFromSlug (sourceSlug: SourceSlug) = $"{sourceSlug}.derived.json"

let getDerivedFeedKeyFromSource (sourceSetting: SourceSetting) =
    getDerivedFeedKeyFromSlug sourceSetting.SourceSlug

let crossItemEquivalent (item: RssItem) (batchItem: DerivedItem) =
    item.Guid = batchItem.Item.Guid
    && item.Link = batchItem.Item.Link

let getFreshSourceItems (storage: ObjectStorageService) (sourceSetting: SourceSetting) (incomingRssItems: RssItem array) =
    fio {
        let feedKey = getDerivedFeedKeyFromSource sourceSetting
        let! maybeDerivedS3Object = FIO.awaitTask (storage.GetObject feedKey) id

        match maybeDerivedS3Object with
        | Some derivedS3Object ->
            let derivedFeed = deserialise<DerivedFeed> derivedS3Object.Content

            let submittedBatchItems =
                derivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

            if Array.length submittedBatchItems = 0 then
                return! FIO.succeed (Array.truncate sourceSetting.MaximumLookback incomingRssItems)
            else
                let truncationIndex =
                    incomingRssItems
                    |> Array.tryFindIndexBack (fun incomingItem ->
                        submittedBatchItems
                        |> Array.tryFind (crossItemEquivalent incomingItem)
                        |> Option.isSome)
                    |> Option.defaultValue sourceSetting.MaximumLookback

                return! FIO.succeed (
                    incomingRssItems
                    |> Array.truncate truncationIndex
                    |> Array.filter (fun sourceItem ->
                        let maybeMatch =
                            submittedBatchItems
                            |> Array.tryFind (fun batchItem -> crossItemEquivalent sourceItem batchItem)

                        maybeMatch.IsNone))
        | None -> return! FIO.succeed (Array.truncate sourceSetting.MaximumLookback incomingRssItems)
    }

let submitSummaryBatch
    (storage: ObjectStorageService)
    (source: SourceSetting)
    (fetchSource: string -> Task<RssItem array>)
    (modelActions: LanguageModelActions)
    =
    fio {
        let! sourceItems = FIO.awaitTask (fetchSource source.SourceUrl) id
        let! submittedSourceItems = getFreshSourceItems storage source sourceItems

        if Array.length submittedSourceItems > 0 then
            let submitBatchParameters =
                { SystemPrompt = Option.defaultValue defaultSystemPrompt source.SystemPrompt
                  InputTokenCutoff = source.InputTokenCutoff
                  OutputTokenCutoff = source.OutputTokenCutoff }

            let! summaryRequest = FIO.awaitTask (modelActions.SubmitBatch submittedSourceItems submitBatchParameters) id

            return! FIO.succeed (Some summaryRequest)
        else
            return! FIO.succeed None
    }

let retryHttp (times: int) (effect: FIO.DSL.FIO<Result<'a, Net.HttpStatusCode>, 'e>) =
    let rec loop attempt =
        fio {
            let! res = effect
            match res with
            | Ok x -> return Ok x
            | Error e ->
                if attempt < times then
                    return! loop (attempt + 1)
                else
                    return Error e
        }
    loop 1

let appendToFeed (storage: ObjectStorageService) (sourceSetting: SourceSetting) (derivedBatch: DerivedBatch) =
    let append =
        fio {
            let feedKey = getDerivedFeedKeyFromSource sourceSetting
            let! maybeDerivedS3Object = FIO.awaitTask (storage.GetObject feedKey) id

            match maybeDerivedS3Object with
            | Some derivedS3Object ->
                let existingDerivedFeed = deserialise<DerivedFeed> derivedS3Object.Content

                let updatedDerivedFeed =
                    { SourceUrl = existingDerivedFeed.SourceUrl
                      Batches = Array.append existingDerivedFeed.Batches [| derivedBatch |] }

                return! FIO.awaitTask (storage.PutObject feedKey (serialise updatedDerivedFeed) (Some derivedS3Object.ETag)) id
            | None ->
                let derivedFeed =
                    { SourceUrl = sourceSetting.SourceUrl
                      Batches = [| derivedBatch |] }

                return! FIO.awaitTask (storage.PutObject feedKey (serialise derivedFeed) None) id
        }

    retryHttp 3 append

let feedUpdateWithSummaryRequests storage source fetchSource modelActions =
    fio {
        let! maybeSubmitBatch = submitSummaryBatch storage source fetchSource modelActions
        match maybeSubmitBatch with
        | Some batch ->
            let! b = appendToFeed storage source batch
            return! FIO.succeed (Some b)
        | None -> return! FIO.succeed None
    }

let tryFeedUpdateWithSummaryResults (storage: ObjectStorageService) sourceSetting modelActions =
    let update =
        fio {
            let feedKey = getDerivedFeedKeyFromSource sourceSetting
            let! maybeDerivedS3Object = FIO.awaitTask (storage.GetObject feedKey) id

            match maybeDerivedS3Object with
            | Some s3Object ->
                let existingDerivedFeed = deserialise<DerivedFeed> s3Object.Content
                let! maybeUpdatedDerivedFeed = FIO.awaitTask (modelActions.GetUpdatedDerivedFeed sourceSetting existingDerivedFeed) id

                match maybeUpdatedDerivedFeed with
                | Some updated ->
                    let! putResult = FIO.awaitTask (storage.PutObject feedKey (serialise updated) (Some s3Object.ETag)) id

                    let getUpdatedCount batches =
                        batches
                        |> Array.map _.BatchItems
                        |> Array.concat
                        |> Array.filter _.Result.IsSome
                        |> Array.length

                    return putResult |> Result.map (fun _ ->
                            getUpdatedCount updated.Batches - getUpdatedCount existingDerivedFeed.Batches)

                | None -> return Ok 0
            | None -> return Error Net.HttpStatusCode.NotFound
        }

    retryHttp 3 update


