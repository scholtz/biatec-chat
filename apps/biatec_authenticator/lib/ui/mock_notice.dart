import 'package:flutter/material.dart';

import '../config.dart';
import '../theme.dart';

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
                'Mock Biatec OIDC \u00b7 ${config.oidcBaseUrl}',
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
