# Pinned model catalog

`catalog.json` contains the 59 ASR entries selected by T3's catalog generator at commit `a03d9bce026ef11930b35e9855adc1cdb5ea6eb7`. The native engine version is transcribe.cpp 0.4.1. Catalog presence does not mean that TypeWhisper has tested every model or GPU.

The metadata source is `@handy-computer/models` 0.2.3. Its published archive is:

```text
https://registry.npmjs.org/@handy-computer/models/-/models-0.2.3.tgz
SHA-256 ddda8875ea2709a61dba3a95c181ec08238a0de2ca67e30a59b936875328f23f
Package gitHead 2d77f8810ee9539a90328a4c59b420326b225d39
```

The package declares MIT for its metadata. Model weights have separate publisher terms. TypeWhisper downloads weights only on request and does not include them in the plugin.

## Model and license review

Each entry retains the conversion repository, exact revision, root filename, size, SHA-256, architecture, minimum engine version, and capabilities. These come from the pinned Handy archive. The supported architecture list matches transcribe.cpp 0.4.1's ASR registry. The T3 filter excludes non-ASR entries, noncommercial licenses, unsupported architectures, newer engine requirements, and community-licensed multilingual Moonshine variants.

The original publisher was resolved from the conversion's pinned Hugging Face model card. Each entry records that publisher's checked revision, license identifier, and repository link at that revision. The publisher identifier takes precedence over the conversion's label. For example, Whisper Large V3 Turbo has an MIT publisher label even though Handy labels the conversion Apache 2.0. The catalog does not treat a conversion label as a license grant.

Known standard license identifiers can be downloaded. Custom or unknown publisher terms remain listed with an unavailable reason until those terms are reviewed. This includes the models whose original publishers label them `other`. Downloading or linking must not bypass this gate. A license identifier does not replace the publisher's license text or required notices.

Review catalog updates against the pinned native engine before changing the source version. Recheck publisher terms, metadata conflicts, ASR roles, minimum versions, and file digests. Do not update revisions from a live catalog when settings open.

## Reference measurements

Accuracy values are the Handy package's FLEURS test error percentages for Q8_0 weights. Speed values are the package's reference seconds per 30 seconds of audio on Ryzen 7 4750U, using CPU or Vulkan. Each result includes its source and conditions.

These values do not measure local GPU compatibility, first-preview delay, mixed-language dictation, or required memory. Size means download size, not RAM or VRAM.

## Storage

Downloads resume into a stable `.partial` file. The store checks disk space and verifies the complete size and SHA-256 before atomic publication. A separate receipt records verification. Readiness checks use the receipt and file metadata so UI refreshes do not hash large models. Loading rehashes the complete file and holds the model's cross-process lock until its lease is released.

Linking validates an existing file and records external ownership. The store never moves, changes permissions on, or deletes that target. Removing a linked model removes the receipt and any files in that model's owned storage location only. Managed staging files and receipts cannot be symbolic links.
