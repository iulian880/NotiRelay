---
paths:
  - "tests/**"
  - "src/**/*.cs"
---
# Tests
- `tests/NotiRelay.Tests` targets plain `net10.0` and has no ProjectReference to the WPF app. Each platform-neutral source is linked in `NotiRelay.Tests.csproj`: `<Compile Include="../../src/X.cs" Link="Linked/X.cs" />`.
- A new testable class must stay platform-neutral (no WPF, WinForms, WinRT or COM types) and be linked there by hand, or its tests won't compile.
- Keep Windows-only calls behind a thin wrapper so the logic stays testable on macOS and Linux.
