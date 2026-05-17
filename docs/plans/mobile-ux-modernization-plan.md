# Mobile UX Modernization Plan

This plan is written for a fast implementation pass by GPT-5.3 Spark or a similar smaller coding model. Keep the scope mobile-only unless the task explicitly says otherwise.

## Chosen Direction

Use the **Review Queue** concept as the product direction. The default post-load surface is not a month gallery; it is a queue of new photos with `New`, `Saved`, and `All` filters. The app must finish scanning the full photo list before showing saved/new/selected counters so counts do not climb independently while the load is still incomplete.

## Goal

Make `ProcareDownloader.Mobile` feel like a modern, task-focused mobile photo utility:

- Sign in through the embedded Procare browser with clear progress feedback.
- Choose a student from a simple, tappable list.
- Review new photos first, with saved and all-photo filters available nearby.
- Select individual photos, select a date group, select the active filter, save new, or save selected without hunting through menus.
- Configure save layout from a bottom sheet that explains the current path and cache state.
- View full-size photos in a dark, full-screen viewer with swipe, zoom, and simple navigation.

## Current Architecture

Main files:

- `ProcareDownloader.Mobile/MainPage.xaml`
- `ProcareDownloader.Mobile/MainPage.xaml.cs`
- `ProcareDownloader.Mobile/ViewModels/MainPageViewModel.cs`
- `ProcareDownloader.Mobile/Resources/Styles/Colors.xaml`
- `ProcareDownloader.Mobile/Resources/Styles/Styles.xaml`

The mobile app is currently a single MAUI `ContentPage`. It layers login, student selection, gallery, settings, photo viewer, and loading overlays in one page. Keep that structure for now so the refactor stays fast and low-risk. Split components later only after the new shell is stable.

## UX Structure

Use these screen states:

1. Login
   - Top card: "Sign in with Procare" and a short explanation.
   - Main area: full-width embedded `WebView`.
   - Loading status should appear after session detection.

2. Loading
   - Centered activity indicator.
   - Strong status line from `StatusMessage`.
   - Muted supporting detail from `SubStatus`.

3. Student Selection
   - Header: "Choose a student".
   - Student cards with photo/avatar, name, and "Open photo library".
   - Entire card is tappable.

4. Review Queue
   - Compact app bar with back, student avatar, title, settings, and select-all.
   - Tabs: `New`, `Saved`, `All`.
   - Summary card with final total, saved, new, and selected counts after loading completes.
   - Date-group queue rows with a small preview strip and group-level selection.
   - Expanded rows may show a wrapped thumbnail grid, but they must stay collapsed by default.
   - Bottom action bar with selected count, select/deselect active filter, and save action.

5. Settings
   - Bottom-sheet overlay.
   - Save layout picker.
   - Preview path.
   - Storage/cache information.
   - Clear cache and sign out actions.

6. Photo Viewer
   - Dark full-screen overlay.
   - Photo title/date at top.
   - Close action.
   - Full image with pinch/pan/double-tap support from `MainPage.xaml.cs`.
   - Previous/next controls at bottom.

## Implementation Checklist

1. Keep the app mobile-first.
   - Use one restrained palette: light background, white surfaces, teal primary, neutral ink, neutral border.
   - Avoid decorative hero sections or marketing copy.
   - Use 44px or larger tap targets.

2. Replace confusing glyphs.
   - Avoid mojibake and unsupported icon fonts.
   - Use plain text labels such as `Back`, `Tune`, `Close`, `Saved`, `v`, and `>`.
   - If adding icon assets later, add them as MAUI image resources.

3. Make configuration obvious.
   - Keep settings reachable from the gallery app bar.
   - Show the selected layout preview inside settings.
   - Keep cache/storage details near cache actions.

4. Make saving obvious.
   - Bottom bar must always show selected count when gallery is visible.
   - Primary action: save selected.
   - Secondary action: save unsaved.
   - Button labels should include counts where useful.

5. Use view-model properties for dynamic labels.
   - Keep `UnsavedDownloadButtonText`.
   - Add or keep `DownloadSelectedButtonText`.
   - Add or keep `PrimaryDownloadButtonText`, `NewTabText`, `SavedTabText`, and `AllTabText`.
   - Refresh those computed properties in `RefreshCounts()`.

6. Keep loading separate from final counters.
   - While loading, show only `Loaded X of Y photos` or `Found X photos so far`.
   - Do not show `New`, `Saved`, `All`, selected count, or save actions until the full list has been applied.
   - If a stale cache is used after refresh failure, make that explicit in `SubStatus`.

7. Verify after every layout change.
   - Build Android target: `dotnet build ProcareDownloader.Mobile/ProcareDownloader.Mobile.csproj -f net10.0-android`.
   - If possible, run in Android emulator and capture screenshots for login, student list, gallery, settings, and viewer.
   - Check narrow width for button text clipping and overlapping.

## Follow-Up Refactor

After the current clean shell builds:

1. Split `MainPage.xaml` into XAML resource dictionaries and small reusable controls only if the file becomes hard to edit.
2. Move mobile color/style tokens into `Resources/Styles/Colors.xaml` and `Styles.xaml`.
3. Replace text-based controls with image buttons only after adding actual cross-platform image resources.
4. Consider a virtualized grid implementation if large libraries show scrolling lag.
5. Add UI tests or snapshot checks around the gallery and settings overlay once the visual structure stabilizes.

## Acceptance Criteria

- Android build passes.
- No mojibake appears in mobile UI source.
- Login, student selection, gallery, settings, and photo viewer all remain reachable from the existing commands.
- Save layout changes still persist through `SettingsService`.
- Existing download commands are unchanged at the service layer.
- The desktop WPF app is not changed.
