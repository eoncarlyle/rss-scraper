module DomainModel

open System
open System.Text.Json.Serialization
open System.Threading.Tasks

type ProcessingStatus =
    | InProgress
    | Canceling
    | Ended

type RssItem =
    { Title: String
      Guid: String option
      Link: String option
      Description: String
      Content: String
      PubDate: DateTimeOffset option }

type DerivedItem =
    { Guid: String
      Included: Boolean
      Item: RssItem
      Result: String option }

type DerivedBatch =
    { Id: String
      ProcessingStatus: ProcessingStatus
      BatchItems: DerivedItem array }

type DerivedFeed =
    { SourceUrl: String
      Batches: DerivedBatch array }

type SourceSlug =
    | [<JsonName "artemis">] Artemis
    | [<JsonName "grocery-dive">] GroceryDive
    | [<JsonName "c-store-dive">] CStoreDive
    | [<JsonName "supply-chain-dive">] SupplyChainDive

type LangaugeModel =
    | [<JsonName "claude-haiku-4-5">] ClaudeHaiku45
    | [<JsonName "gemini-2-5-flash-lite">] Gemini25FlashLite

type SourceSetting =
    { SourceUrl: string
      SourceSlug: SourceSlug
      Model: LangaugeModel 
      SystemPrompt: string option
      InputTokenCutoff: int
      OutputTokenCutoff: int
      MaximumItems: int
      MaximumLookback: int
      Synchronous: bool option
      Enabled: bool }

type SourceSettings = { Sources: SourceSetting array }

type SummaryRequestParameters =
    { SystemPrompt: string
      InputTokenCutoff: int
      OutputTokenCutoff: int }

type LangaugeModelActions =
    { SubmitBatch: RssItem array -> SummaryRequestParameters -> Task<DerivedBatch>
      GetUpdatedDerivedFeed: SourceSetting -> DerivedFeed -> Task<DerivedFeed option> }

type S3Object = { Content: String; ETag: String }

type ItemTokenRecord =
    { MinimalRssItem: RssItem
      TokenCount: Int32
      Guid: Guid }
    
type SinkFeed =
    {
       SinkSlug: string
       SourceSlugs: SourceSlug array
       
    }