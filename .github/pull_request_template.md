## What this changes

<!-- One or two sentences. What problem does this solve? -->

## Why

<!-- Context a reviewer won't have. Link an issue if there is one. -->

## Verification

<!-- What you actually ran, not what you intend to run. -->

- [ ] `dotnet build SolidWorksPartMatcher.sln` is clean
- [ ] `dotnet test SolidWorksPartMatcher.sln` passes
- [ ] Exercised the affected flow in the app

<!-- Call out anything you could NOT verify locally (SOLIDWORKS COM behaviour,
     the packaged release, real part files), so the reviewer knows where the
     risk actually sits. -->

## Matching accuracy

- [ ] This change cannot cause an uncertain pair to be merged automatically
- [ ] Not applicable — this change doesn't touch matching
