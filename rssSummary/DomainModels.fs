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

type SourceSlug =
    | [<JsonName "artemis">] Artemis
    | [<JsonName "grocery-dive">] GroceryDive
    | [<JsonName "c-store-dive">] CStoreDive
    | [<JsonName "supply-chain-dive">] SupplyChainDive

type LangaugeModel =
    | [<JsonName "claude-haiku-4-5">] ClaudeHaiku45
    | [<JsonName "gemini-2-5-flash-lite">] Gemini25FlashLite

type SourceConfig =
    { SourceUrl: string
      SourceSlug: SourceSlug
      Model: LangaugeModel option
      SystemPrompt: string option
      InputTokenCutoff: int
      OutputTokenCutoff: int
      MaximumLookback: int
      Enabled: bool }

type SourcesConfiguration = { Sources: SourceConfig array }

type SubmitBatchParameters =
    { SystemPrompt: string
      InputTokenCutoff: int
      OutputTokenCutoff: int }


type LangaugeModelActions =
    { SubmitBatch: MinimalRssItem array -> SubmitBatchParameters -> Task<SourceFeedSummaryRequestBatch>
      GetUpdatedDerivedFeed: SourceConfig -> DerivedSourceFeed -> Task<DerivedSourceFeed option> }

type S3Object = { Content: String; ETag: String }
