module Models

open System
open FSharp.Data
open Anthropic.Models.Messages.Batches

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
    { ID: String
      ProcessingStatus: ProcessingStatus
      ResultsUrl: String option
      BatchItems: BatchRssItem array }

type DerivedSourceFeedInternal =
    { SourceUrl: String
      Batches: SourceFeedSummaryRequestBatch array }

type DerivedSourceFeed = { Channel: DerivedSourceFeedInternal }

type SourcesConfiguration = XmlProvider<"SourcesConfiguration.xml">

type ProviderDerivedSourceFeed = XmlProvider<"DerivedSourceFeed.xml">
