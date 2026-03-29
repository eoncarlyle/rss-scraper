module Serialisation

open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open DomainModels

let jsonOptions =
    let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    options.WriteIndented <- true
    options.Converters.Add(JsonFSharpConverter(
        JsonFSharpOptions.Default()
            .WithUnionUnwrapFieldlessTags()
            .WithSkippableOptionFields()))
    options

let serializeDerivedSourceFeed (feed: DerivedSourceFeed) : string =
    JsonSerializer.Serialize(feed, jsonOptions)

let deserializeDerivedSourceFeed (json: string) : DerivedSourceFeed =
    JsonSerializer.Deserialize<DerivedSourceFeed>(json, jsonOptions)

let serializeSourcesConfiguration (config: SourcesConfiguration) : string =
    JsonSerializer.Serialize(config, jsonOptions)

let deserializeSourcesConfiguration (json: string) : SourcesConfiguration =
    JsonSerializer.Deserialize<SourcesConfiguration>(json, jsonOptions)

// SDK interop
let toAnthropicStatus (status: ProcessingStatus) =
    match status with
    | InProgress -> Anthropic.Models.Messages.Batches.ProcessingStatus.InProgress
    | Canceling -> Anthropic.Models.Messages.Batches.ProcessingStatus.Canceling
    | Ended -> Anthropic.Models.Messages.Batches.ProcessingStatus.Ended

let fromAnthropicStatus (status: Anthropic.Models.Messages.Batches.ProcessingStatus) =
    match status with
    | Anthropic.Models.Messages.Batches.ProcessingStatus.InProgress -> InProgress
    | Anthropic.Models.Messages.Batches.ProcessingStatus.Canceling -> Canceling
    | _ -> Ended

let htmlSanitized (s: string) =
    let doc = HtmlAgilityPack.HtmlDocument()
    doc.LoadHtml s
    doc.DocumentNode.InnerText

let firstSentenceRemove (s: string) =
    // TODO: this parsing is bad and you know it
    let secondMatch = Regex.Matches(s, "^(.+)")[0]
    let firstSentence = secondMatch.Groups[1].Value
    s.Replace(firstSentence, "")
