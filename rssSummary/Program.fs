open System
open System.Threading.Tasks
open Giraffe.ViewEngine

[<EntryPoint>]
let main args =
    let systemPrompt =
        """
            You are summarising material for someone not working in the field who wants to stay up to speed with the
            content. Use only ASCII characters and format in plain text - not markdown. Draw inspiration from Matt
            Yglesias, Bryne Hobart, Ben Thompson, and Patrick McKenzie in your explainations. This summary is just for
            personal use, I am absolutely not trying to pass off someone else's intellectual property as my own.
        """
            .Replace("\n", " ")

    task {
        let! rssItems = OriginalSourceFeeds.Artemis.getRssItems "https://www.artemis.bm/feed/"
        let! sourceFeedBatch = Anthropic.submitStandardBatch rssItems systemPrompt
        let xml = Serialisation.serializeBatch sourceFeedBatch

        System.IO.File.WriteAllText("/tmp/myFile.xml", xml |> RenderView.AsString.xmlNode)
        Console.WriteLine rssItems
    }
    |> Task.WaitAll

    0
