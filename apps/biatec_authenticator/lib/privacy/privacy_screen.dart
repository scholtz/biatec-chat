import 'package:flutter/material.dart';

import '../security/settings_frame.dart' show confirmSheet;
import '../theme.dart';
import '../ui/ui.dart';
import 'phone_eraser.dart';
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
    final yes = await confirmSheet(
      context,
      title: 'Erase everything on this phone?',
      message: 'This removes your two-factor accounts, the device key, your '
          'enrollment, your approval history and your settings from this '
          'phone, and signs the device out. The app goes back to the start.',
      warning: 'This cannot be undone. Your two-factor accounts are not stored '
          'anywhere else: add them again at each service, or restore them '
          'from your own backup file. Biatec OIDC keeps its record of this '
          'device until you cancel it from another device.',
      confirmLabel: 'Erase everything',
      cancelLabel: 'Keep my data',
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

  Widget _item(DataItem i) => ListTile(
        title: Text(i.title),
        subtitle: Text(i.text),
        isThreeLine: true,
      );

  @override
  Widget build(BuildContext context) {
    final result = _result;
    return Scaffold(
      appBar: AppBar(title: const Text('Privacy')),
      body: ListView(
        key: const Key('privacy-list'),
        children: [
          const Padding(
            padding: EdgeInsets.fromLTRB(
                AppSpacing.m, AppSpacing.m, AppSpacing.m, 0),
            child: Text('Biatec Authenticator collects only what it needs to '
                'work. This is all of it.'),
          ),
          _section(context, 'Stored on this phone'),
          for (final i in storedOnPhone) _item(i),
          _section(context, 'Sent to Biatec OIDC'),
          for (final i in sentToBiatec) _item(i),
          _section(context, 'Sent to the push service'),
          for (final i in sentToPushService) _item(i),
          _section(context, 'Never collected'),
          for (final t in neverCollected)
            ListTile(
              leading: const ExcludeSemantics(child: Icon(Icons.block)),
              title: Text(t),
            ),
          if (widget.eraser != null) ...[
            _section(context, 'Erase'),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: AppSpacing.m),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const Text('Removes everything above from this phone. It '
                      'does not remove what Biatec OIDC holds about your '
                      'account; cancel this device from another device for '
                      'that.'),
                  const SizedBox(height: AppSpacing.m),
                  if (_refused)
                    const Padding(
                      padding: EdgeInsets.only(bottom: AppSpacing.m),
                      child: StatusBanner(
                        key: Key('erase-refused'),
                        severity: StatusSeverity.warning,
                        title: 'Nothing was erased',
                        message: 'The fingerprint or face check did not pass.',
                      ),
                    ),
                  if (result != null && !result.complete)
                    Padding(
                      padding: const EdgeInsets.only(bottom: AppSpacing.m),
                      child: StatusBanner(
                        key: const Key('erase-incomplete'),
                        severity: StatusSeverity.danger,
                        title: 'Some data could not be removed',
                        message: '${result.failed.join('. ')}. Try again, or '
                            'uninstall the app, which removes the rest.',
                      ),
                    ),
                  OutlinedButton.icon(
                    key: const Key('erase-everything'),
                    onPressed: _busy ? null : _erase,
                    icon: const Icon(Icons.delete_forever_outlined),
                    label: Text(
                        _busy ? 'Erasing' : 'Erase everything on this phone'),
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
