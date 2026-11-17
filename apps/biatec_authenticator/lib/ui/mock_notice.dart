import 'package:material_ui/material_ui.dart';

import '../config.dart';
import '../theme.dart';
import '../l10n/common_texts.dart';

/// A strip that marks a build talking to the mock Biatec OIDC, never to a real server.
class MockNotice extends StatelessWidget {
  const MockNotice({super.key, required this.config});

  final AppConfig config;

  @override
  Widget build(BuildContext context) {
    final colors = context.statusColors;
    return Semantics(
      container: true,
      child: ColoredBox(
        color: colors.warning,
        child: Padding(
          padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.m, vertical: AppSpacing.xs),
          child: Row(children: [
            Icon(Icons.science_outlined, size: 16, color: colors.onWarning),
            const SizedBox(width: AppSpacing.s),
            Expanded(
              child: Text(
                CommonTexts.of(Localizations.maybeLocaleOf(context)).text('mockNotice', {'url': config.oidcBaseUrl}),
                key: const Key('mock-notice'),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context)
                    .textTheme
                    .labelSmall
                    ?.copyWith(color: colors.onWarning),
              ),
            ),
          ]),
        ),
      ),
    );
  }
}
