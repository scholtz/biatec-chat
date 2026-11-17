import 'package:material_ui/material_ui.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../theme.dart';
import '../../ui/ui.dart';
import '../codes_texts.dart';

/// Opens a scanner and returns the text of the first QR code it reads, or null when the user goes back. Replaced in tests.
typedef QrScannerBuilder = Widget Function(
    BuildContext context, ValueChanged<String> onScanned);

/// The camera view. The camera permission is asked when this screen opens and not before, and no picture is kept: the frames are read for a code and dropped.
class QrScanScreen extends StatefulWidget {
  const QrScanScreen({super.key, this.builder = cameraScanner});

  final QrScannerBuilder builder;

  @override
  State<QrScanScreen> createState() => _QrScanScreenState();

  /// The real camera.
  static Widget cameraScanner(
          BuildContext context, ValueChanged<String> onScanned) =>
      _CameraView(onScanned: onScanned);
}

class _QrScanScreenState extends State<QrScanScreen> {
  bool _done = false;

  void _scanned(String text) {
    if (_done) return;
    _done = true;
    Navigator.of(context).pop(text);
  }

  @override
  Widget build(BuildContext context) {
    final t = CodesTexts.from(context);
    return Scaffold(
      appBar: AppBar(title: Text(t.text('scanQr'))),
      body: Column(children: [
        Expanded(child: widget.builder(context, _scanned)),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.m),
          child: Text(t.text('scanHelp')),
        ),
      ]),
    );
  }
}

class _CameraView extends StatefulWidget {
  const _CameraView({required this.onScanned});

  final ValueChanged<String> onScanned;

  @override
  State<_CameraView> createState() => _CameraViewState();
}

class _CameraViewState extends State<_CameraView> {
  final MobileScannerController _controller = MobileScannerController(
    formats: const [BarcodeFormat.qrCode],
    detectionSpeed: DetectionSpeed.noDuplicates,
  );

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MobileScanner(
      controller: _controller,
      onDetect: (capture) {
        for (final code in capture.barcodes) {
          final text = code.rawValue;
          if (text != null) {
            widget.onScanned(text);
            return;
          }
        }
      },
      errorBuilder: (context, error) => ErrorState(
        title: CodesTexts.from(context).text('cameraUnavailable'),
        message: CodesTexts.from(context).text(
            error.errorCode == MobileScannerErrorCode.permissionDenied
                ? 'cameraDenied'
                : 'cameraOther'),
      ),
    );
  }
}
