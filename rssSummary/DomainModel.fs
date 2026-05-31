module DomainModel

open System
open System.Text.Json.Serialization
open System.Threading.Tasks


type SourceSlug =
    | [<JsonName "artemis">] Artemis
    | [<JsonName "grocery-dive">] GroceryDive
    | [<JsonName "c-store-dive">] CStoreDive
    | [<JsonName "supply-chain-dive">] SupplyChainDive
    | [<JsonName "the-zvi">] TheZvi
    | [<JsonName "noahpinion">] Noahpinion
    | [<JsonName "china-talk">] ChinaTalk
    | [<JsonName "marginal-revolution">] MarginalRevolution

    override this.ToString() =
        match this with
        | Artemis -> "artemis"
        | GroceryDive -> "grocery-dive"
        | CStoreDive -> "c-store-dive"
        | SupplyChainDive -> "supply-chain-dive"
        | TheZvi -> "the-zvi"
        | Noahpinion -> "noahpinion"
        | ChinaTalk -> "china-talk"
        | MarginalRevolution -> "marginal-revolution"

type SinkSlug =
    | [<JsonName "artemis">] Artemis
    | [<JsonName "industry-dive">] IndustryDive
    | [<JsonName "substack-general">] SubstackGeneral
    | [<JsonName "marginal-revolution">] MarginalRevolution

    override this.ToString() =
        match this with
        | Artemis -> "artemis"
        | IndustryDive -> "industry-dive"
        | SubstackGeneral -> "substack-general"
        | MarginalRevolution -> "marginal-revolution"

type ProcessingStatus =
    | InProgress
    | Canceling
    | Ended

type RssItem =
    { Title: String
      Guid: String option
      Link: String option
      Description: String
      Content: String // Content is only really used for the derived items
      PubDate: String option } //It is worth thinking about splitting out the RSS items

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

type LanguageModel =
    | [<JsonName "claude-haiku-4-5">] ClaudeHaiku45
    | [<JsonName "gemini-2.5-flash-lite">] Gemini25FlashLite
    | [<JsonName "gemini-3.1-flash-lite">] Gemini31FlashLite

[<CLIMutable>]
type SourceSettings = { Sources: SourceSetting array }

and [<CLIMutable>] SourceSetting =
    { SourceUrl: string
      SourceSlug: SourceSlug
      Model: LanguageModel
      SystemPrompt: string option
      InputTokenCutoff: int
      OutputTokenCutoff: int
      MaximumItems: int
      MaximumLookback: int
      Synchronous: bool option
      Enabled: bool }

type SummaryRequestParameters =
    { SystemPrompt: string
      InputTokenCutoff: int
      OutputTokenCutoff: int }

type LanguageModelActions =
    { SubmitBatch: RssItem array -> SummaryRequestParameters -> Task<DerivedBatch>
      GetUpdatedDerivedFeed: SourceSetting -> DerivedFeed -> Task<DerivedFeed option> }

type S3Object = { Content: String; ETag: String }

type ItemTokenRecord =
    { MinimalRssItem: RssItem
      TokenCount: Int32
      Guid: Guid }

[<CLIMutable>]
type SinkSetting =
    { SinkSlug: SinkSlug
      SourceSlugs: SourceSlug array
      SourceItemsPerPublish: int
      MaximumItems: int }

[<CLIMutable>]
type SinkSettings = { Sinks: SinkSetting array }

type SinkFeed =
    { Title: string
      Link: string
      PubDate: String
      Description: string
      Items: SinkItem array }

and SinkItem =
    { Item: RssItem
      DerivedItemReferences: DerivedItemReference array }

and DerivedItemReference =
    { Title: String
      Guid: String option
      Link: String option }
