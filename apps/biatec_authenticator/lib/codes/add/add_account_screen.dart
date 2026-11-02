import 'package:flutter/material.dart';

import '../../theme.dart';
import '../../totp/core/otp_account.dart';
import '../../ui/ui.dart';
import '../../totp/vault/vault_key_store.dart';
import '../codes_controller.dart';
import 'account_input.dart';
import 'qr_scan_screen.dart';

enum _Step { choose, link, key, preview }

/// Which way to start in, when the empty Codes tab offers the three ways directly.
enum AddMethod { scan, link, key }

/// Adds an account: by scanning a QR code, by pasting an `otpauth://` link, or by typing the setup key. Whatever the way, the account is shown (who it is and how its codes are made) before anything is saved, a copy of an account already in the list is refused, and a refused entry says why in words.
class AddAccountScreen extends StatefulWidget {
  const AddAccountScreen(
      {super.key,
      required this.controller,
      this.scanner = QrScanScreen.cameraScanner,
      this.start});

  final CodesController controller;
  final QrScannerBuilder scanner;
  final AddMethod? start;

  @override
  State<AddAccountScreen> createState() => _AddAccountScreenState();
}

class _AddAccountScreenState extends State<AddAccountScreen> {
  _Step _step = _Step.choose;
  OtpAccount? _account;
  String? _problem;
  bool _saving = false;

  final _link = TextEditingController();
  final _issuer = TextEditingController();
  final _label = TextEditingController();
  final _secret = TextEditingController();
  OtpAlgorithm _algorithm = OtpAlgorithm.sha1;
  int _digits = 6;
  int _period = OtpLimits.defaultPeriod;

  @override
  void initState() {
    super.initState();
    switch (widget.start) {
      case AddMethod.link:
        _step = _Step.link;
      case AddMethod.key:
        _step = _Step.key;
      case AddMethod.scan:
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _scan();
        });
      case null:
        break;
    }
  }

  @override
  void dispose() {
    _account?.wipe();
    _link.dispose();
    _issuer.dispose();
    _label.dispose();
    _secret.dispose();
    super.dispose();
  }

  void _preview(OtpAccount account) {
    _account?.wipe();
    setState(() {
      _account = account;
      _problem = null;
      _step = _Step.preview;
    });
  }

  void _try(OtpAccount Function() make) {
    try {
      _preview(make());
    } on InputProblem catch (e) {
      setState(() => _problem = e.message);
    }
  }

  Future<void> _scan() async {
    final text = await Navigator.of(context).push<String>(MaterialPageRoute(
        builder: (_) => QrScanScreen(builder: widget.scanner)));
    if (text == null || !mounted) return;
    try {
      _preview(AccountInput.fromLink(text));
    } on InputProblem catch (e) {
      setState(() {
        _problem = 'That QR code is not a two-factor account. ${e.message}';
        _step = _Step.choose;
      });
    }
  }

  Future<void> _save() async {
    final account = _account;
    if (account == null || _saving) return;
    setState(() {
      _saving = true;
      _problem = null;
    });
    try {
      await widget.controller.add(account);
      if (mounted) Navigator.of(context).pop(true);
    } on AddRefused catch (e) {
      _stop(e.message);
    } on VaultAuthFailed {
      _stop('The fingerprint or face check did not pass. Nothing was saved.');
    } on VaultBiometricsChanged {
      _stop('Your fingerprints or face changed. Nothing was saved.');
    } on Object {
      _stop('The account could not be saved. Try again.');
    }
  }

  void _stop(String message) {
    if (!mounted) return;
    setState(() {
      _saving = false;
      _problem = message;
    });
  }

  @override
  Widget build(BuildContext context) {
    final title = switch (_step) {
      _Step.choose => 'Add an account',
      _Step.link => 'Paste a link',
      _Step.key => 'Enter a setup key',
      _Step.preview => 'Check the account',
    };
    return PopScope(
      canPop: _step == _Step.choose,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) {
          setState(() {
            _problem = null;
            _step = _Step.choose;
          });
        }
      },
      child: Scaffold(
        appBar: AppBar(title: Text(title)),
        body: ListView(
          padding: const EdgeInsets.all(AppSpacing.m),
          children: [
            if (_problem != null)
              Padding(
                padding: const EdgeInsets.only(bottom: AppSpacing.m),
                child: Semantics(
                  liveRegion: true,
                  child: StatusBanner(
                    key: const Key('add-problem'),
                    severity: StatusSeverity.danger,
                    title: 'Not added',
                    message: _problem!,
                  ),
                ),
              ),
            ...switch (_step) {
              _Step.choose => _choose(),
              _Step.link => _linkForm(),
              _Step.key => _keyForm(),
              _Step.preview => _previewCard(),
            },
          ],
        ),
      ),
    );
  }

  List<Widget> _choose() => [
        const Padding(
          padding: EdgeInsets.only(bottom: AppSpacing.s),
          child: Text('Pick the way your service gives you the account.'),
        ),
        _Way(
            keyName: 'add-scan',
            icon: Icons.qr_code_scanner,
            title: 'Scan a QR code',
            text: 'The camera is asked for only now. No picture is kept.',
            onTap: _scan),
        _Way(
            keyName: 'add-link',
            icon: Icons.link,
            title: 'Paste a link',
            text: 'A link that starts with otpauth://',
            onTap: () => setState(() {
                  _problem = null;
                  _step = _Step.link;
                })),
        _Way(
            keyName: 'add-key',
            icon: Icons.keyboard,
            title: 'Enter a setup key',
            text:
                'The letters and digits the service shows next to the QR code.',
            onTap: () => setState(() {
                  _problem = null;
                  _step = _Step.key;
                })),
      ];

  List<Widget> _linkForm() => [
        TextField(
          key: const Key('link-field'),
          controller: _link,
          autocorrect: false,
          enableSuggestions: false,
          keyboardType: TextInputType.url,
          minLines: 2,
          maxLines: 4,
          decoration: const InputDecoration(
              labelText: 'otpauth:// link',
              helperText: 'Paste the whole link.'),
        ),
        const SizedBox(height: AppSpacing.l),
        FilledButton(
          key: const Key('link-continue'),
          onPressed: () => _try(() => AccountInput.fromLink(_link.text)),
          child: const Text('Continue'),
        ),
      ];

  List<Widget> _keyForm() => [
        TextField(
          key: const Key('key-issuer'),
          controller: _issuer,
          textInputAction: TextInputAction.next,
          decoration: const InputDecoration(
              labelText: 'Service', helperText: 'For example GitHub.'),
        ),
        const SizedBox(height: AppSpacing.m),
        TextField(
          key: const Key('key-label'),
          controller: _label,
          textInputAction: TextInputAction.next,
          decoration: const InputDecoration(
              labelText: 'Account name',
              helperText: 'Usually your email address.'),
        ),
        const SizedBox(height: AppSpacing.m),
        TextField(
          key: const Key('key-secret'),
          controller: _secret,
          autocorrect: false,
          enableSuggestions: false,
          decoration: const InputDecoration(
              labelText: 'Setup key',
              helperText: 'Spaces and capital letters do not matter.'),
        ),
        const SizedBox(height: AppSpacing.m),
        ExpansionTile(
          key: const Key('key-advanced'),
          title: const Text('More settings'),
          subtitle: const Text('Only change these if the service says so.'),
          childrenPadding: const EdgeInsets.only(bottom: AppSpacing.s),
          children: [
            DropdownButtonFormField<OtpAlgorithm>(
              key: const Key('key-algorithm'),
              initialValue: _algorithm,
              decoration: const InputDecoration(labelText: 'Algorithm'),
              items: [
                for (final a in OtpAlgorithm.values)
                  DropdownMenuItem(value: a, child: Text(a.uriName)),
              ],
              onChanged: (v) => setState(() => _algorithm = v ?? _algorithm),
            ),
            const SizedBox(height: AppSpacing.m),
            DropdownButtonFormField<int>(
              key: const Key('key-digits'),
              initialValue: _digits,
              decoration: const InputDecoration(labelText: 'Digits'),
              items: [
                for (var d = OtpLimits.minDigits; d <= OtpLimits.maxDigits; d++)
                  DropdownMenuItem(value: d, child: Text('$d')),
              ],
              onChanged: (v) => setState(() => _digits = v ?? _digits),
            ),
            const SizedBox(height: AppSpacing.m),
            DropdownButtonFormField<int>(
              key: const Key('key-period'),
              initialValue: _period,
              decoration:
                  const InputDecoration(labelText: 'Seconds each code lasts'),
              items: [
                for (final p in const [15, 30, 60, 90, 120])
                  DropdownMenuItem(value: p, child: Text('$p')),
              ],
              onChanged: (v) => setState(() => _period = v ?? _period),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.l),
        FilledButton(
          key: const Key('key-continue'),
          onPressed: () => _try(() => AccountInput.fromKey(
                issuer: _issuer.text,
                label: _label.text,
                key: _secret.text,
                algorithm: _algorithm,
                digits: _digits,
                period: _period,
              )),
          child: const Text('Continue'),
        ),
      ];

  List<Widget> _previewCard() {
    final account = _account!;
    final theme = Theme.of(context);
    final hotp = account.type == OtpType.hotp;
    return [
      Card(
        key: const Key('add-preview'),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.m),
          child:
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(account.issuer.isEmpty ? 'No service name' : account.issuer,
                style: theme.textTheme.titleLarge),
            Text(account.label, style: theme.textTheme.titleMedium),
            const SizedBox(height: AppSpacing.m),
            Text(hotp
                ? 'Counter-based, starting at ${account.counter}'
                : 'Time-based, a new code every ${account.period} seconds'),
            Text('${account.digits} digits, ${account.algorithm.uriName}'),
            Text('Key strength: ${account.secretBits} bits'),
          ]),
        ),
      ),
      const SizedBox(height: AppSpacing.s),
      const Text('Check that this is the account you meant. Once it is saved '
          'the key is never shown again.'),
      const SizedBox(height: AppSpacing.l),
      FilledButton(
        key: const Key('save-account'),
        onPressed: _saving ? null : _save,
        child: Text(_saving ? 'Saving' : 'Add account'),
      ),
    ];
  }
}

class _Way extends StatelessWidget {
  const _Way(
      {required this.keyName,
      required this.icon,
      required this.title,
      required this.text,
      required this.onTap});

  final String keyName;
  final IconData icon;
  final String title;
  final String text;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: ListTile(
        key: Key(keyName),
        leading: Icon(icon),
        title: Text(title),
        subtitle: Text(text),
        trailing: const Icon(Icons.chevron_right),
        onTap: onTap,
      ),
    );
  }
}
