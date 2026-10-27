use super::{CallError, CallFailure, ErrorCode, call_error};

fn failure(code: ErrorCode) -> CallFailure {
    CallFailure {
        code,
        message: "the backend's words".into(),
    }
}

#[test]
fn a_failed_call_keeps_its_code_and_the_backends_message() {
    let words = || "the backend's words".to_string();
    assert_eq!(
        call_error(failure(ErrorCode::NotFound)),
        CallError::NotFound(words())
    );
    assert_eq!(
        call_error(failure(ErrorCode::BadArgs)),
        CallError::BadArgs(words())
    );
    assert_eq!(
        call_error(failure(ErrorCode::Failed)),
        CallError::Failed(words())
    );
    assert_eq!(
        call_error(failure(ErrorCode::Timeout)),
        CallError::Timeout(words())
    );
}

#[test]
fn an_unreachable_machine_says_which_machine() {
    let code = ErrorCode::Unreachable {
        machine: "studio".into(),
    };
    assert_eq!(
        call_error(failure(code)),
        CallError::Unreachable {
            machine: "studio".into()
        }
    );
}
