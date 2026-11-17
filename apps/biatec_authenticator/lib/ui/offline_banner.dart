import 'package:material_ui/material_ui.dart';

import 'status_banner.dart';
import '../l10n/common_texts.dart';

/// Shown when Biatec OIDC cannot be reached. Approvals need a live fetch, so a stale request is never signed.
class OfflineBanner extends StatelessWidget {
  const OfflineBanner({super.key, this.onRetry});

  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    final t = CommonTexts.of(Localizations.maybeLocaleOf(context));
    return StatusBanner(
      severity: StatusSeverity.warning,
      icon: Icons.cloud_off,
      title: t.text('offlineTitle'),
      message: t.text('offlineMessage'),
      actionLabel: onRetry == null ? null : t.text('tryAgain'),
      onAction: onRetry,
    );
  }
}
