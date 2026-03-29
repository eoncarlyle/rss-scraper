module AppGemini

open DomainModels

let submitBatch (items: MinimalRssItem array) systemPrompt =
    failwith "not implemented"


let getUpdatedDerivedFeed (source: SourceConfig) (derivedSourceFeed: DerivedSourceFeed) : DerivedSourceFeed =
    failwith "not implemented"

let AppGeminiActions: LangaugeModelActions =
    { SubmitBatch = submitBatch
      GetUpdatedDerivedFeed = getUpdatedDerivedFeed }
