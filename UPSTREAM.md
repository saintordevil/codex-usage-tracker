# Source provenance

Source: https://github.com/robinebers/openusage

Reviewed and ported commit: `66aada23bfbac5c5988617fdd6a0f8edbe106764`.

Copyright (c) 2026 Robin Ebers. Original code is used under the included MIT license. Usage Dock is an independently maintained Windows derivative. The white OpenAI symbol identifies the Codex provider and comes from the original `codex.svg` asset.

| Original | Windows adaptation |
|---|---|
| CodexUsageMapper.swift | Core/QuotaMapper.cs |
| CodexLogFileParser.swift, CodexLogUsageScanner.swift | Core/LogParser.cs, Core/HistoryReader.cs |
| CodexLogUsageScanner+Pricing.swift, CodexUsagePricing.swift | Core/Pricing.cs |
| Pace.swift | Core/Pace.cs |
| Theme.swift, DensitySetting.swift | App/Ui.cs, per-density layout constants |
| DashboardView.swift, PopoverFooter.swift, HeaderView.swift | App/MainWindow.cs |
| TotalSpendCard.swift, ProviderSectionHeader.swift, WidgetRowView.swift, UsageSparkline.swift | App/DashboardView.cs |
| ModelUsageDetail.swift, UsageTrendDetail.swift | App/DetailViews.cs |
| SettingsScreen.swift, CustomizeRow.swift | App/SettingsView.cs |

The original Swift files used as references are retained under `upstream`. Additional visual reference: https://www.openusage.ai/, captured and measured on 12 October 2026. Its panel is 340 CSS pixels wide with 1.5 line height, while the native Swift source uses 320 logical points. The Windows panel follows the supplied website's visual scale.

Windows-specific changes: WPF host and notification-area icon; official Codex app-server for authentication recovery instead of rewriting auth files; bounded incremental Windows file reader; reduced Codex-only scope; no Spark; no analytics, iCloud, Sparkle updater, or HTTP listener; reset credits are read-only. The original settings and layout patterns are retained for applicable features. Startup is optional and only enabled by the user. Public screenshots use synthetic offline example data.

Pricing inputs: OpenUsage's bundled LiteLLM/models.dev catalogs plus its pricing supplement. Current rates checked against https://developers.openai.com/api/docs/pricing. Upstream source attribution and software licenses are preserved; no upstream application binary is redistributed.
