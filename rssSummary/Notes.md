# Notes
- Seperate sources from sinks
- Place all feeds in object storage to be able to run anywhere (use Tigris, make use of eTags if possible)
- Consider if makes sense to seperate normalisation in source feed setup
- Place the Quartz scheduling inside the dependency injection (although the Giraffe server could be seperated out)
- Need to start using eTags on object storage for concurrency control

- Probably should come up with real dependency injection soon
- Need to do exponential backoff
- Need to truncate sink feeds

- Previous
  - Basten async inference doesn't store jobs, but is about 1/https://docs.baseten.co/inference/async
  - If necessary, run some stuff from residential IP address
  - https://docs.mistral.ai/capabilities/batch#inline-batching
  - https://docs.mistral.ai/api/endpoint/beta/conversations
  - Figure out multi-model later
    - It would be way simpler if everything under my perview - the deserialisation and serialisation is becoming a 
      nightmare.