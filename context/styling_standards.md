# Modern Windows 11 Styling Standards (WPF)

This document outlines the UI/UX tokens and theme standards for the PoE2 Market Filter Overlay.

## Styling Principles

1. **Fluent Aesthetics**: Respect Windows 11 Fluent Design principles (rounded corners, subtle shadows, Segoe UI Variable font).
2. **Overlay Usability**: Maintain readability under variable transparency levels. Use contrasting border borders and dark background panels.
3. **Harmonious Palette**: Use HSL-tailored colors inspired by Path of Exile 2:
   - **Primary Accent**: `#FF6124` (PoE2 Fire/Orange)
   - **Secondary Accent**: `#E5B560` (Gold/Valuable)
   - **Background**: `#1E1E1E` (Dark Slate grey with opacity)
   - **Hover State**: `#323232` (Light Slate grey)
   - **Text Primary**: `#FFFFFF`
   - **Text Muted**: `#AAAAAA`

## Control Styling Standards

- **Window Layout**:
  - `WindowStyle="None"`
  - `AllowsTransparency="True"`
  - `Background="#D9181818"` (roughly 85% opacity Slate Grey)
- **Corner Radius**:
  - Uniform `8` for minor controls (buttons, inputs).
  - Uniform `12` for primary panels and windows.
- **Micro-Animations**:
  - Opacity and color transitions over `150ms` on hover states.
