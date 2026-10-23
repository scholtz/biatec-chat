import UIKit

/// Hides the app's content from the app switcher snapshot and while the screen is recorded or mirrored (BCHAT-031): a cover view is put over
/// every window of the app when it resigns active or when the screen is captured, and removed again afterwards.
final class ScreenCover {
    static let shared = ScreenCover()
    private var covers: [UIView] = []
    private var started = false

    func start() {
        guard !started else { return }
        started = true
        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(refresh), name: UIApplication.willResignActiveNotification, object: nil)
        center.addObserver(self, selector: #selector(refresh), name: UIApplication.didBecomeActiveNotification, object: nil)
        center.addObserver(self, selector: #selector(refresh), name: UIScreen.capturedDidChangeNotification, object: nil)
    }

    @objc private func refresh() {
        let inactive = UIApplication.shared.applicationState != .active
        if inactive || isCaptured() { show() } else { hide() }
    }

    private func isCaptured() -> Bool {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .contains { $0.screen.isCaptured }
    }

    private func show() {
        guard covers.isEmpty else { return }
        for scene in UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene }) {
            for window in scene.windows {
                let cover = UIView(frame: window.bounds)
                cover.autoresizingMask = [.flexibleWidth, .flexibleHeight]
                cover.backgroundColor = .systemBackground
                window.addSubview(cover)
                covers.append(cover)
            }
        }
    }

    private func hide() {
        covers.forEach { $0.removeFromSuperview() }
        covers.removeAll()
    }
}
