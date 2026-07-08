# Contributing to Fuseo

Thanks for your interest! Fuseo is a native macOS tool for redacting ID documents (SwiftUI + Apple's Vision / Core Image / PDFKit), MIT-licensed, © ATI Inc. Its redaction engine, **MaskingCore**, is a Swift Package with no third-party dependencies.

## Project layout

- `Sources/MaskingCore/` — the redaction engine (Model / Support / Pipeline / Render / Resources). This is the open-source core.
- `Sources/poc/` — a measurement/regression CLI that drives the pipeline on sample images.
- `Fuseo/` — the macOS app target (SwiftUI UI built on MaskingCore).
- `project.yml` — [XcodeGen](https://github.com/yonaskolb/XcodeGen) spec (the `.xcodeproj` is generated, not committed).
- `scripts/build-app.sh` — Release build → `/Applications/Fuseo.app`.
- `docs/` — the design contract you must read before touching code: [core-design.md](docs/core-design.md) (coordinate rules, schema, pipeline I/O) and [user-manual.md](docs/user-manual.md).

## Build & test

```sh
brew install xcodegen           # once
swift test                      # run the MaskingCore test suite
swift run -q poc <image ...>    # run the detection pipeline on sample images
xcodegen generate && open Fuseo.xcodeproj   # build & run the app in Xcode
# or, for a Release build installed to /Applications:
./scripts/build-app.sh
```

- Requires a full Xcode install (Command Line Tools alone are not enough), macOS 14+.
- Set your signing team in `project.yml` (`DEVELOPMENT_TEAM`).
- **Always run `xcodegen generate` after adding/removing/renaming source files** — the project is generated from `project.yml`.

## Guidelines

- **Read [docs/core-design.md](docs/core-design.md) before writing code.** It is the implementation contract.
- **Coordinates only through `Support/CoordinateSpace`.** The internal coordinate system is normalized, bottom-left origin; never hand-convert. Preset JSON fixed regions are the only `yTop` (top-left) exception, converted by the loader.
- **Keep the redaction model intact.** Masks must be a raster burn-in (opaque pixel replacement) — never an annotation or a removable layer. Exports must not carry metadata, and masked text must be excluded from searchable-PDF text layers.
- **Keep everything on-device.** No network calls to process documents, no analytics, no telemetry.
- **Never remove the pre-save confirmation UI**, and don't market the tool as "automatically perfect." Automatic detection produces *candidates*; the user confirms.
- **Every preset rule needs a `basis`** (the rationale shown in the confirmation UI).
- **Do not commit real documents.** The `fixtures-private/` directory holds real ID photos and is gitignored — never add it to a commit. If you need test fixtures, generate synthetic images.
- After changes, run `swift test` and the `poc` regression to confirm you haven't regressed detection.
- Match the surrounding code style, prefer native SwiftUI controls, and add a short rationale in PR descriptions.

## Versioning

- `MARKETING_VERSION` in `project.yml` is the human version (bump for meaningful changes).
- The build number is the git commit count (set automatically by `scripts/build-app.sh`).

## License of contributions

By contributing, you agree your contributions are licensed under the project's [MIT License](LICENSE).
