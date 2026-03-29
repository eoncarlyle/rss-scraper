module DomainModels

open System
open System.Text.Json.Serialization
open System.Threading.Tasks

type ProcessingStatus =
    | InProgress
    | Canceling
    | Ended

type MinimalRssItem =
    { Title: String
      Guid: String option
      Link: String option
      Description: String
      Content: String
      PubDate: DateTimeOffset option }

type BatchRssItem =
    { Guid: String
      Included: Boolean
      Item: MinimalRssItem
      Result: String option }

type SourceFeedSummaryRequestBatch =
    { Id: String
      ProcessingStatus: ProcessingStatus
      BatchItems: BatchRssItem array }

type DerivedSourceFeed =
    { SourceUrl: String
      Batches: SourceFeedSummaryRequestBatch array }

type LangaugeModel =
    | [<JsonName "claude-haiku-4-5">] ClaudeHaiku45
    | [<JsonName "gemini-2-5-flash-lite">] Gemini25FlashLite

type SourceConfig =
    { SourceUrl: string
      SystemPrompt: string option
      SourceSlug: string
      MaximumLookback: int
      Model: LangaugeModel option
      Enabled: bool }

type SourcesConfiguration = { Sources: SourceConfig array }

type LangaugeModelActions =
    {
      SubmitBatch: MinimalRssItem array -> string -> Task<SourceFeedSummaryRequestBatch>
      GetUpdatedDerivedFeed: SourceConfig -> DerivedSourceFeed -> Task<DerivedSourceFeed option> }
