open System
open System.Threading.Tasks
open DerivedSourceFeeds
open Queries

[<EntryPoint>]
let main args =

    let firstSource = Array.get sourcesConfiguration.Sources 0
    
    
    
    task {
        let! incomingRssItems = OriginalSourceFeeds.Artemis.getRssItems firstSource.SourceUrl
        //let! requestBatch = Anthropic.submitStandardBatch incomingRssItems systemPrompt
        
        let! a = getFeedUpdate firstSource
        
           
        Console.WriteLine()
        // TODO: assessing if request *should* be made
        //updateDerivedSourceFeed firstSource requestBatch
        ()
        //Console.WriteLine rssItems
    }
    |> Task.WaitAll

    0
