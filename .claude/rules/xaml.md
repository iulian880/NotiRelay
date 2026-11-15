---
paths:
  - "src/**/*.xaml"
  - "src/**/*.xaml.cs"
---
# XAML conventions
- Colours come from the Palette brushes in `src/App.xaml` (`{StaticResource InkBrush}`, `AccentBrush`, ...). Don't add new hex literals; if a colour is missing, add a named brush to the Palette first.
- Text on white keeps at least 4.5:1 contrast; the muted/subtle brushes are tuned for that.
- Reuse the existing styles (`Card`, `SectionTitle`, `FieldLabel`, `Primary`, `DangerButton`, `SmallButton`, ...) before adding new ones. All resources live in `App.xaml`; there are no separate ResourceDictionaries.
