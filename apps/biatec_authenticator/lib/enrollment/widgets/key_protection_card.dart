import 'package:flutter/material.dart';

import '../../keys/key_security_level.dart';
import '../../ui/ui.dart';
import '../enrollment_strings.dart';

/// Where the device key is kept, in words and with an icon (never by color alone).
class KeyProtectionCard extends StatelessWidget {
  const KeyProtectionCard({super.key, required this.level, this.strings});

  final KeySecurityLevel level;

  /// The texts of the warning; the device's language when null.
  final EnrollStrings? strings;

  @override
  Widget build(BuildContext context) {
    if (level.isHardware) {
      return StatusBanner(
        severity: StatusSeverity.success,
        icon: Icons.verified_user_outlined,
        title: 'Hardware-protected key',
        message: 'Key protection: ${level.label}. The key cannot be exported.',
      );
    }
    if (level == KeySecurityLevel.windowsAccount) {
      // A Windows computer without a usable TPM: enrolled, and the user is told what that means and what would make it safer.
      final strings = this.strings ?? EnrollStrings.of();
      return StatusBanner(
        key: const Key('software-key-warning'),
        severity: StatusSeverity.warning,
        icon: Icons.gpp_maybe_outlined,
        title: strings.softwareKeyTitle,
        message: '${strings.softwareKey}\n\n${strings.softwareKeyTips}',
      );
    }
    if (level == KeySecurityLevel.software) {
      return StatusBanner(
        severity: StatusSeverity.danger,
        icon: Icons.gpp_bad_outlined,
        title: 'Software-only key',
        message: 'Key protection: ${level.label}. This is not secure hardware, '
            'so it is not accepted.',
      );
    }
    return const StatusBanner(
      severity: StatusSeverity.warning,
      icon: Icons.help_outline,
      title: 'Key protection unknown',
      message: 'Key protection: unknown. This device did not say where the '
          'key is kept.',
    );
  }
}
