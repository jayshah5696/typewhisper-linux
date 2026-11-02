# TypeWhisper Linux Knowledge Graph

TypeWhisper Linux is a desktop speech-to-text dictation application built on .NET 10 and Avalonia, featuring local/cloud ASR plugins, speculative cleanup, and native Linux desktop delivery.

This knowledge graph serves as the structured architectural and behavioral reference for TypeWhisper Linux, maintained with [lat.md](https://www.lat.md).

## Knowledge Graph Documents

Explore the core architectural domains and operational workflows of TypeWhisper Linux.

- [[architecture]] — High-level system structure, components, and project boundaries
- [[dictation-pipeline]] — Audio capture, speech recognition, live cleanup, and text delivery
- [[plugin-system]] — PluginSDK contracts, provider lifecycle, and hardware acceleration
- [[desktop-linux]] — Linux display server integration, global hotkeys, overlays, and packaging
- [[verification]] — Five verification layers, isolated test harness, and quality benchmarks

## Grounding Rules for Agents

Before changing code, consult relevant graph sections to understand component design intent.

- Run `lat search <query>` or `lat locate <query>` to locate relevant documentation.
- Maintain graph accuracy when implementing new features or changing architectural invariants.
- Ensure all references pass verification with `lat check`.
