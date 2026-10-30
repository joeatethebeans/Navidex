# Changelog

What changed in each release of Melodex. Versions follow [semantic versioning](https://semver.org).

## Unreleased

Everything on the way to 1.0.

### New

- **Melodex**: Navidex has a new name, a logo and a pink theme. Installs from before upgrade in place; see
  [upgrading](docs/guide/upgrading.md#from-navidex).
- **Themes**: pink, green, blue, grey, purple and amber, each light or dark or following your device, a black mode
  for OLED screens, and high contrast with colour-blind-safe status colours. Each user chooses their own under
  Settings → Appearance.
- **The guide**, in `docs/guide` and in the web app under Settings → Help.
- **Users**: accounts for everyone, with permissions to browse, request, listen, review, format, remove, scan, change
  settings, make API keys and manage users. Everyone signs in; Remember me keeps a browser signed in for 30 days.
- **API keys** belong to the user who made them and can do no more than they may.
- **Review**: exclude, defer or hide files, and put them back from the lists at the bottom.
- **Your own music**: excluded files show as their tags say, with their own artist and album pages and covers.
- **Formatting**: rewrite files' tags, covers and folders from the catalogue, for a track, album, artist or the whole
  library.
- **Scan menu**: match unmatched files again, or everything, and format the library.
- **Tasks** can be paused, cancelled and reordered; tracks have pages of their own.
- **About**, under Settings: the version, what it was built from, and links to GitHub.

### Fixed

- Browsers could keep an old copy of the web app's page, showing *Loading Navidex* and failing to change the theme.
- The ownership meters and legend use brighter colours in light themes.
- The Log page's Info, Warnings and Errors toggles have tick boxes in their own colours, not black.

### Changed

- API keys start `mdx_`; `ndx_` keys still work.
- Webhook headers are `Melodex-Signature`, `Melodex-Event` and `Melodex-Delivery`.
- The local network is no longer let in without signing in.
