#include "pin_dialog.h"

#include <cstring>
#include <vector>

namespace pin_dialog {
namespace {

constexpr WORD kMessageId = 100;
constexpr WORD kLabelId = 101;
constexpr WORD kPinId = 102;
constexpr WORD kRepeatLabelId = 103;
constexpr WORD kRepeatId = 104;
constexpr WORD kErrorId = 105;

constexpr UINT_PTR kTimeoutTimer = 1;
// Shorter than the two minutes the Dart side waits for a prompt (LockController.promptTimeout), so the dialog is gone before Dart gives up on it.
constexpr UINT kTimeoutMs = 90 * 1000;

// The predefined window classes of a dialog template.
constexpr WORD kButtonClass = 0x0080;
constexpr WORD kEditClass = 0x0081;
constexpr WORD kStaticClass = 0x0082;

struct Texts {
  const wchar_t* label;
  const wchar_t* repeat;
  const wchar_t* too_short;
  const wchar_t* different;
  const wchar_t* cancel;
  const wchar_t* create;
};

const Texts kEnglish = {
    L"PIN of this app",
    L"The same PIN again",
    L"The PIN needs at least 6 characters.",
    L"The two PINs are not the same.",
    L"Cancel",
    L"Windows Hello cannot be used on this computer, so choose a PIN for this app (at least 6 characters). The security chip (TPM) of this computer "
    L"guards it: the app asks for it to unlock and to approve, and the chip locks for a while after too many wrong tries. The PIN cannot be recovered. If "
    L"you forget it, this computer has to be enrolled again.",
};

const Texts kSlovak = {
    L"PIN tejto aplikácie",
    L"Ten istý PIN ešte raz",
    L"PIN musí mať najmenej 6 znakov.",
    L"Zadané PIN kódy sa nezhodujú.",
    L"Zrušiť",
    L"Windows Hello sa na tomto počítači nedá použiť, preto si zvoľte PIN pre túto aplikáciu (najmenej 6 znakov). "
    L"Stráži ho bezpečnostný čip (TPM) tohto počítača: aplikácia ho žiada pri odomknutí a pri "
    L"schvaľovaní a po priveľa nesprávnych pokusoch sa čip na čas zamkne. PIN sa nedá obnoviť. Ak ho zabudnete, "
    L"tento počítač treba zaregistrovať znova.",
};

const Texts& TextsOfUser() {
  return PRIMARYLANGID(::GetUserDefaultUILanguage()) == LANG_SLOVAK ? kSlovak : kEnglish;
}

// A dialog template built in memory (the runner has no dialog resource): the header and the items, as DialogBoxIndirectParam reads them.
class Template {
 public:
  Template(DWORD style, int width, int height, const wchar_t* title) {
    Dword(style);
    Dword(0);  // extended style
    count_at_ = data_.size();
    Word(0);  // number of items, filled in by Add
    Word(0);  // x
    Word(0);  // y
    Word(static_cast<WORD>(width));
    Word(static_cast<WORD>(height));
    Word(0);  // no menu
    Word(0);  // the standard dialog class
    Text(title);
    Word(9);  // DS_SETFONT: point size and face
    Text(L"Segoe UI");
  }

  void Add(WORD window_class, DWORD style, int x, int y, int width, int height, int id,
           const wchar_t* text) {
    if (data_.size() % 2 != 0) Word(0);  // every item starts on a DWORD boundary
    Dword(style | WS_CHILD | WS_VISIBLE);
    Dword(0);
    Word(static_cast<WORD>(x));
    Word(static_cast<WORD>(y));
    Word(static_cast<WORD>(width));
    Word(static_cast<WORD>(height));
    Word(static_cast<WORD>(id));
    Word(0xFFFF);
    Word(window_class);
    Text(text);
    Word(0);  // no creation data
    data_[count_at_]++;
  }

  const DLGTEMPLATE* get() const {
    return reinterpret_cast<const DLGTEMPLATE*>(data_.data());
  }

 private:
  void Word(WORD value) { data_.push_back(value); }
  void Dword(DWORD value) {
    Word(LOWORD(value));
    Word(HIWORD(value));
  }
  void Text(const wchar_t* text) {
    for (; *text != L'\0'; text++) Word(static_cast<WORD>(*text));
    Word(0);
  }

  // WORDs: the buffer of a vector is aligned for a DWORD, and the header is padded by hand.
  std::vector<WORD> data_;
  size_t count_at_ = 0;
};

struct State {
  Mode mode;
  const std::wstring* message;
  const Texts* texts;
  std::wstring pin;
};

// Reads a field into `out` and wipes the copy on the stack.
void ReadField(HWND dialog, int id, std::wstring* out) {
  wchar_t buffer[kMaxLength + 2] = {};
  const UINT length = ::GetDlgItemTextW(dialog, id, buffer, static_cast<int>(kMaxLength + 1));
  out->assign(buffer, length);
  ::SecureZeroMemory(buffer, sizeof(buffer));
}

void Wipe(std::wstring* text) {
  if (!text->empty()) ::SecureZeroMemory(text->data(), text->size() * sizeof(wchar_t));
  text->clear();
}

void Close(HWND dialog, INT_PTR answer) {
  ::KillTimer(dialog, kTimeoutTimer);
  // The fields do not keep the PIN after the dialog.
  ::SetDlgItemTextW(dialog, kPinId, L"");
  ::SetDlgItemTextW(dialog, kRepeatId, L"");
  ::EndDialog(dialog, answer);
}

void CenterOver(HWND dialog, HWND owner) {
  RECT own = {};
  RECT over = {};
  if (owner == nullptr || !::GetWindowRect(owner, &over) || !::GetWindowRect(dialog, &own)) return;
  const int x = over.left + ((over.right - over.left) - (own.right - own.left)) / 2;
  const int y = over.top + ((over.bottom - over.top) - (own.bottom - own.top)) / 2;
  ::SetWindowPos(dialog, nullptr, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
}

INT_PTR CALLBACK Procedure(HWND dialog, UINT message, WPARAM wparam, LPARAM lparam) {
  switch (message) {
    case WM_INITDIALOG: {
      ::SetWindowLongPtrW(dialog, DWLP_USER, lparam);
      const State* state = reinterpret_cast<const State*>(lparam);
      ::SetDlgItemTextW(dialog, kMessageId, state->message->c_str());
      ::SendDlgItemMessageW(dialog, kPinId, EM_SETLIMITTEXT, kMaxLength, 0);
      ::SendDlgItemMessageW(dialog, kRepeatId, EM_SETLIMITTEXT, kMaxLength, 0);
      CenterOver(dialog, ::GetParent(dialog));
      ::SetTimer(dialog, kTimeoutTimer, kTimeoutMs, nullptr);
      ::SetForegroundWindow(dialog);
      return TRUE;  // the focus goes to the first field
    }
    case WM_TIMER:
      if (wparam == kTimeoutTimer) Close(dialog, 0);
      return TRUE;
    case WM_COMMAND: {
      State* state = reinterpret_cast<State*>(::GetWindowLongPtrW(dialog, DWLP_USER));
      if (state == nullptr) return FALSE;
      if (LOWORD(wparam) == IDCANCEL) {
        Close(dialog, 0);
        return TRUE;
      }
      if (LOWORD(wparam) != IDOK) return FALSE;
      std::wstring pin;
      ReadField(dialog, kPinId, &pin);
      if (state->mode == Mode::kCreate) {
        // Only a new PIN is held to the rules here. An existing one is whatever the TPM accepts.
        const wchar_t* problem = nullptr;
        std::wstring repeat;
        ReadField(dialog, kRepeatId, &repeat);
        if (pin.size() < kMinLength) {
          problem = state->texts->too_short;
        } else if (pin != repeat) {
          problem = state->texts->different;
        }
        Wipe(&repeat);
        if (problem != nullptr) {
          Wipe(&pin);
          ::SetDlgItemTextW(dialog, kErrorId, problem);
          ::SetFocus(::GetDlgItem(dialog, kPinId));
          return TRUE;
        }
      } else if (pin.empty()) {
        return TRUE;
      }
      state->pin = pin;
      Wipe(&pin);
      Close(dialog, 1);
      return TRUE;
    }
    default:
      return FALSE;
  }
}

}  // namespace

std::wstring CreateMessage() { return TextsOfUser().create; }

std::optional<Pin> Ask(HWND owner, Mode mode, const std::wstring& message) {
  const Texts& texts = TextsOfUser();
  const bool create = mode == Mode::kCreate;
  // Sizes in dialog units. The message of a new PIN is several lines long.
  const int width = 230;
  const int inner = width - 14;
  const int message_height = create ? 68 : 28;
  int y = 7;
  const int message_y = y;
  y += message_height + 6;
  const int label_y = y;
  y += 10;
  const int pin_y = y;
  y += 18;
  int repeat_label_y = 0;
  int repeat_y = 0;
  if (create) {
    repeat_label_y = y;
    y += 10;
    repeat_y = y;
    y += 18;
  }
  const int error_y = y;
  y += 14;
  const int buttons_y = y;
  const int height = y + 14 + 7;

  Template dialog(DS_MODALFRAME | DS_SETFONT | WS_POPUP | WS_CAPTION | WS_SYSMENU, width,
                  height, L"Biatec Authenticator");
  const DWORD field = ES_PASSWORD | ES_AUTOHSCROLL | WS_BORDER | WS_TABSTOP;
  // The first field is the first item that takes the focus.
  dialog.Add(kEditClass, field, 7, pin_y, inner, 13, kPinId, L"");
  if (create) {
    dialog.Add(kEditClass, field, 7, repeat_y, inner, 13, kRepeatId, L"");
    dialog.Add(kStaticClass, SS_LEFT, 7, repeat_label_y, inner, 9, kRepeatLabelId, texts.repeat);
  }
  dialog.Add(kButtonClass, BS_DEFPUSHBUTTON | WS_TABSTOP, width - 7 - 50 - 5 - 50, buttons_y,
             50, 14, IDOK, L"OK");
  dialog.Add(kButtonClass, BS_PUSHBUTTON | WS_TABSTOP, width - 7 - 50, buttons_y, 50, 14,
             IDCANCEL, texts.cancel);
  dialog.Add(kStaticClass, SS_LEFT, 7, message_y, inner, message_height, kMessageId, L"");
  dialog.Add(kStaticClass, SS_LEFT, 7, label_y, inner, 9, kLabelId, texts.label);
  dialog.Add(kStaticClass, SS_LEFT, 7, error_y, inner, 10, kErrorId, L"");

  State state{mode, &message, &texts, {}};
  const INT_PTR answer = ::DialogBoxIndirectParamW(
      ::GetModuleHandleW(nullptr), dialog.get(), owner, Procedure,
      reinterpret_cast<LPARAM>(&state));
  if (answer != 1 || state.pin.empty()) {
    Wipe(&state.pin);
    return std::nullopt;
  }
  Pin pin(state.pin);
  Wipe(&state.pin);
  return pin;
}

}  // namespace pin_dialog
