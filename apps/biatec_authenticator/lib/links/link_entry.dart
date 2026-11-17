import 'package:material_ui/material_ui.dart';

import '../codes/add/qr_scan_screen.dart';
import '../theme.dart';
import 'link_screen.dart';
import 'link_services.dart';
import '../text/rp_strings.dart';

/// "Link a service" in Settings, Linked services: the scanner and, for a phone with no camera (or a code that was sent as text), a field to paste the code into. Both lead to the same place as a deep link
/// from another app: [openLinkCode], so nothing is linked without the link screen.
class LinkEntry extends StatelessWidget {
  const LinkEntry({super.key, required this.services, this.onLinked});

  final LinkServices services;

  /// Called after a service was linked (the list of linked services reloads).
  final VoidCallback? onLinked;

  Future<void> _scan(BuildContext context) async {
    final text = await Navigator.of(context).push<String>(MaterialPageRoute<String>(
      settings: const RouteSettings(name: '/link/scan'),
      builder: (_) => QrScanScreen(builder: services.scanner),
    ));
    if (text == null || !context.mounted) return;
    await openLinkCode(context, services, text, onLinked: onLinked);
  }

  Future<void> _paste(BuildContext context) async {
    final text = await showDialog<String>(
      context: context,
      builder: (_) => const _PasteDialog(),
    );
    if (text == null || !context.mounted) return;
    await openLinkCode(context, services, text, onLinked: onLinked);
  }

  @override
  Widget build(BuildContext context) {
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
    final theme = Theme.of(context);
    return Padding(
      key: const Key('link-entry'),
      padding: const EdgeInsets.fromLTRB(
          AppSpacing.m, AppSpacing.m, AppSpacing.m, AppSpacing.s),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Text(s.t('linkAServiceHelp'),
            style: theme.textTheme.bodyMedium
                ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
        const SizedBox(height: AppSpacing.s),
        FilledButton.icon(
          key: const Key('link-a-service'),
          onPressed: () => _scan(context),
          icon: const Icon(Icons.qr_code_scanner),
          label: Text(s.t('linkTitle')),
        ),
        TextButton(
          key: const Key('paste-link-code'),
          onPressed: () => _paste(context),
          child: Text(s.t('pasteCode')),
        ),
      ]),
    );
  }
}

class _PasteDialog extends StatefulWidget {
  const _PasteDialog();

  @override
  State<_PasteDialog> createState() => _PasteDialogState();
}

class _PasteDialogState extends State<_PasteDialog> {
  final _text = TextEditingController();

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
    return AlertDialog(
      title: Text(s.t('pasteCode')),
      content: TextField(
        key: const Key('paste-link-field'),
        controller: _text,
        autofocus: true,
        autocorrect: false,
        enableSuggestions: false,
        keyboardType: TextInputType.url,
        decoration: InputDecoration(labelText: s.t('pasteField')),
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: Text(s.t('cancel'))),
        FilledButton(
            key: const Key('paste-link-open'),
            onPressed: () => Navigator.of(context).pop(_text.text.trim()),
            child: Text(s.t('pasteOpen'))),
      ],
    );
  }
}
