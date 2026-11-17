import 'package:material_ui/material_ui.dart';

import '../security/settings_frame.dart' show confirmSheet;
import '../theme.dart';
import '../ui/ui.dart';
import 'phone_eraser.dart';
import 'privacy_texts.dart';
import 'storage_inventory.dart';

/// What the app stores, what it sends and to whom, what it never collects, and the action that removes everything from the phone.
class PrivacyScreen extends StatefulWidget {
  const PrivacyScreen({super.key, this.eraser});

  /// Null where erasing is not offered (a screen shown on its own in a test).
  final PhoneEraser? eraser;

  @override
  State<PrivacyScreen> createState() => _PrivacyScreenState();
}

class _PrivacyScreenState extends State<PrivacyScreen> {
  bool _busy = false;
  EraseResult? _result;
  bool _refused = false;

  Future<void> _erase() async {
    final eraser = widget.eraser;
    if (eraser == null || _busy) return;
    final t = PrivacyTexts.of(Localizations.maybeLocaleOf(context));
    final yes = await confirmSheet(
      context,
      title: t.text('confirmTitle'),
      message: t.text('confirmBody'),
      warning: t.text('confirmWarning'),
      confirmLabel: t.text('confirmErase'),
      cancelLabel: t.text('keepData'),
      confirmKey: const Key('confirm-erase'),
      cancelKey: const Key('keep-data'),
      biometric: true,
      icon: Icons.delete_forever_outlined,
    );
    if (!yes || !mounted) return;
    setState(() {
      _busy = true;
      _result = null;
      _refused = false;
    });
    final result = await eraser.erase();
    if (!mounted) return;
    setState(() {
      _busy = false;
      _refused = result == null;
      _result = result;
    });
  }

  Widget _section(BuildContext context, String title) => Padding(
        padding: const EdgeInsets.fromLTRB(
            AppSpacing.m, AppSpacing.l, AppSpacing.m, AppSpacing.xs),
        child: Semantics(
          header: true,
          child: Text(title,
              style: Theme.of(context)
                  .textTheme
                  .titleSmall
                  ?.copyWith(color: Theme.of(context).colorScheme.primary)),
        ),
      );

  Widget _item(DataItem i, Locale? locale) => ListTile(
        title: Text(i.titleIn(locale)),
        subtitle: Text(i.textIn(locale)),
        isThreeLine: true,
      );

  @override
  Widget build(BuildContext context) {
    final result = _result;
    final locale = Localizations.maybeLocaleOf(context);
    final t = PrivacyTexts.of(locale);
    return Scaffold(
      appBar: AppBar(title: Text(t.text('title'))),
      body: ListView(
        key: const Key('privacy-list'),
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(
                AppSpacing.m, AppSpacing.m, AppSpacing.m, 0),
            child: Text(t.text('intro')),
          ),
          _section(context, t.text('stored')),
          for (final i in storedOnPhone) _item(i, locale),
          _section(context, t.text('sentBiatec')),
          for (final i in sentToBiatec) _item(i, locale),
          _section(context, t.text('sentPush')),
          for (final i in sentToPushService) _item(i, locale),
          _section(context, t.text('never')),
          for (final key in neverCollected)
            ListTile(
              leading: const ExcludeSemantics(child: Icon(Icons.block)),
              title: Text(t.text(key)),
            ),
          if (widget.eraser != null) ...[
            _section(context, t.text('erase')),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: AppSpacing.m),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(t.text('eraseBody')),
                  const SizedBox(height: AppSpacing.m),
                  if (_refused)
                    Padding(
                      padding: const EdgeInsets.only(bottom: AppSpacing.m),
                      child: StatusBanner(
                        key: const Key('erase-refused'),
                        severity: StatusSeverity.warning,
                        title: t.text('refusedTitle'),
                        message: t.text('refusedBody'),
                      ),
                    ),
                  if (result != null && !result.complete)
                    Padding(
                      padding: const EdgeInsets.only(bottom: AppSpacing.m),
                      child: StatusBanner(
                        key: const Key('erase-incomplete'),
                        severity: StatusSeverity.danger,
                        title: t.text('incompleteTitle'),
                        message: t.text('incompleteBody',
                            {'failed': result.failed.join('. ')}),
                      ),
                    ),
                  OutlinedButton.icon(
                    key: const Key('erase-everything'),
                    onPressed: _busy ? null : _erase,
                    icon: const Icon(Icons.delete_forever_outlined),
                    label: Text(t.text(_busy ? 'erasing' : 'eraseButton')),
                  ),
                  const SizedBox(height: AppSpacing.l),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
