// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

/// <summary>
/// Which machine this is, as three strings.
///
/// Lifted out of ModelProfile, which needs WMI to compile because DISCOVERING these is a
/// Windows-specific act. The answer is not: a baseboard product is the key every per-board
/// profile in this project is filed under, and on Linux the same three strings come out of
/// /sys/class/dmi/id/ with no library at all. Keeping the record beside the Windows detector
/// meant a Linux daemon could not name the board it was refusing to write to.
/// </summary>
public sealed record ModelInfo(string Manufacturer, string Product, string BaseboardProduct);
