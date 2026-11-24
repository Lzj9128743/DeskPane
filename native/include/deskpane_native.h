#ifndef DESKPANE_NATIVE_H
#define DESKPANE_NATIVE_H

#include <stdint.h>

#define DESKPANE_NATIVE_ABI_VERSION 2u
#define DESKPANE_EXPLORER_SHELL_LAUNCH_STRUCT_VERSION_1 1u
#define DESKPANE_QUICK_ACCESS_STRUCT_VERSION_1 1u
#define DESKPANE_RECYCLE_BIN_STRUCT_VERSION_1 1u
#define DESKPANE_MUSIC_VOLUME_STRUCT_VERSION_1 1u
#define DESKPANE_NATIVE_STRUCT_VERSION_2 2u
#define DESKPANE_NATIVE_DLL_NAME L"deskpane_native.dll"

#define DESKPANE_NATIVE_CAPABILITY_SHORTCUT_READ_STORED_RAW_V2 (1ull << 0)
#define DESKPANE_NATIVE_CAPABILITY_SHORTCUT_READ_EFFECTIVE_DIAGNOSTIC_V2 (1ull << 1)
#define DESKPANE_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_NO_UI_V2 (1ull << 2)
#define DESKPANE_NATIVE_CAPABILITY_SHORTCUT_WRITE_V2 (1ull << 3)
#define DESKPANE_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_WITH_UI_V2 (1ull << 4)
#define DESKPANE_NATIVE_CAPABILITY_MUSIC_VOLUME_V1 (1ull << 5)
#define DESKPANE_NATIVE_CAPABILITY_EXPLORER_SHELL_LAUNCH_V1 (1ull << 6)
#define DESKPANE_NATIVE_CAPABILITY_QUICK_ACCESS_V1 (1ull << 7)
#define DESKPANE_NATIVE_CAPABILITY_RECYCLE_BIN_V1 (1ull << 8)
#define DESKPANE_NATIVE_CAPABILITIES_STAGE_3C2 \
    (DESKPANE_NATIVE_CAPABILITY_SHORTCUT_READ_STORED_RAW_V2 | \
     DESKPANE_NATIVE_CAPABILITY_SHORTCUT_READ_EFFECTIVE_DIAGNOSTIC_V2 | \
     DESKPANE_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_NO_UI_V2 | \
     DESKPANE_NATIVE_CAPABILITY_SHORTCUT_WRITE_V2 | \
     DESKPANE_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_WITH_UI_V2)
#define DESKPANE_NATIVE_CAPABILITIES_STAGE_4C \
    (DESKPANE_NATIVE_CAPABILITIES_STAGE_3C2 | \
     DESKPANE_NATIVE_CAPABILITY_MUSIC_VOLUME_V1)
#define DESKPANE_NATIVE_CAPABILITIES_STAGE_4D4A \
    (DESKPANE_NATIVE_CAPABILITIES_STAGE_4C | \
     DESKPANE_NATIVE_CAPABILITY_EXPLORER_SHELL_LAUNCH_V1)
#define DESKPANE_NATIVE_CAPABILITIES_STAGE_4D4B \
    (DESKPANE_NATIVE_CAPABILITIES_STAGE_4D4A | \
     DESKPANE_NATIVE_CAPABILITY_QUICK_ACCESS_V1)
#define DESKPANE_NATIVE_CAPABILITIES_STAGE_5B4C1B1 \
    (DESKPANE_NATIVE_CAPABILITIES_STAGE_4D4B | \
     DESKPANE_NATIVE_CAPABILITY_RECYCLE_BIN_V1)
#define DESKPANE_NATIVE_CAPABILITIES DESKPANE_NATIVE_CAPABILITIES_STAGE_5B4C1B1

#define DESKPANE_NATIVE_STATUS_OK 0u
#define DESKPANE_NATIVE_STATUS_INVALID_ARGUMENT 1u
#define DESKPANE_NATIVE_STATUS_INCOMPATIBLE_STRUCT 2u
#define DESKPANE_NATIVE_STATUS_BUFFER_TOO_SMALL 3u
#define DESKPANE_NATIVE_STATUS_COM_INITIALIZATION_FAILED 4u
#define DESKPANE_NATIVE_STATUS_OBJECT_CREATION_FAILED 5u
#define DESKPANE_NATIVE_STATUS_LOAD_FAILED 6u
#define DESKPANE_NATIVE_STATUS_OPERATION_FAILED 7u
#define DESKPANE_NATIVE_STATUS_INTERNAL_ERROR 8u
#define DESKPANE_NATIVE_STATUS_NOT_IMPLEMENTED 9u

#define DESKPANE_NATIVE_S_OK ((int32_t)0x00000000u)
#define DESKPANE_NATIVE_S_FALSE ((int32_t)0x00000001u)
#define DESKPANE_NATIVE_E_NOTIMPL ((int32_t)0x80004001u)
#define DESKPANE_NATIVE_E_INVALIDARG ((int32_t)0x80070057u)
#define DESKPANE_NATIVE_HRESULT_INSUFFICIENT_BUFFER ((int32_t)0x8007007Au)
#define DESKPANE_NATIVE_HRESULT_NOT_ATTEMPTED ((int32_t)0x8000000Au)

#define DESKPANE_SHORTCUT_READ_MODE_STORED_RAW 1u
#define DESKPANE_SHORTCUT_READ_MODE_EFFECTIVE_DIAGNOSTIC 2u
#define DESKPANE_SHORTCUT_WRITE_FLAG_SHELL_NAMESPACE_TARGET (1u << 0)

#define DESKPANE_SHORTCUT_FIELD_TARGET_PATH (1u << 0)
#define DESKPANE_SHORTCUT_FIELD_DESCRIPTION (1u << 1)
#define DESKPANE_SHORTCUT_FIELD_ARGUMENTS (1u << 2)
#define DESKPANE_SHORTCUT_FIELD_WORKING_DIRECTORY (1u << 3)
#define DESKPANE_SHORTCUT_FIELD_ICON_PATH (1u << 4)

#define DESKPANE_SHORTCUT_PHASE_COM_INITIALIZE (1u << 0)
#define DESKPANE_SHORTCUT_PHASE_CREATE_OBJECT (1u << 1)
#define DESKPANE_SHORTCUT_PHASE_LOAD (1u << 2)
#define DESKPANE_SHORTCUT_PHASE_RESOLVE (1u << 3)
#define DESKPANE_SHORTCUT_PHASE_SAVE (1u << 4)

#define DESKPANE_MUSIC_VOLUME_OPERATION_GET_SNAPSHOT 1u
#define DESKPANE_MUSIC_VOLUME_OPERATION_GET_SYSTEM 2u
#define DESKPANE_MUSIC_VOLUME_OPERATION_SET_SYSTEM 3u
#define DESKPANE_MUSIC_VOLUME_OPERATION_SET_SESSION 4u

#define DESKPANE_MUSIC_VOLUME_PHASE_COM_INITIALIZE (1u << 0)
#define DESKPANE_MUSIC_VOLUME_PHASE_CREATE_ENUMERATOR (1u << 1)
#define DESKPANE_MUSIC_VOLUME_PHASE_GET_DEVICE (1u << 2)
#define DESKPANE_MUSIC_VOLUME_PHASE_SYSTEM_VOLUME (1u << 3)
#define DESKPANE_MUSIC_VOLUME_PHASE_ENUMERATE_SESSIONS (1u << 4)
#define DESKPANE_MUSIC_VOLUME_PHASE_SESSION_VOLUME (1u << 5)

#define DESKPANE_MUSIC_VOLUME_MATCH_NONE 0u
#define DESKPANE_MUSIC_VOLUME_MATCH_IDENTIFIER_APP_ID 1u
#define DESKPANE_MUSIC_VOLUME_MATCH_INSTANCE_APP_ID 2u
#define DESKPANE_MUSIC_VOLUME_MATCH_DISPLAY_NAME 3u
#define DESKPANE_MUSIC_VOLUME_MATCH_PROCESS_DISPLAY_NAME 4u
#define DESKPANE_MUSIC_VOLUME_MATCH_APP_ID_PROCESS 5u
#define DESKPANE_MUSIC_VOLUME_MATCH_IDENTIFIER_DISPLAY_NAME 6u
#define DESKPANE_MUSIC_VOLUME_MATCH_SINGLE_FALLBACK 7u

#define DESKPANE_EXPLORER_SHELL_LAUNCH_PHASE_COM_INITIALIZE (1u << 0)
#define DESKPANE_EXPLORER_SHELL_LAUNCH_PHASE_CREATE_OBJECT (1u << 1)
#define DESKPANE_EXPLORER_SHELL_LAUNCH_PHASE_WINDOWS (1u << 2)
#define DESKPANE_EXPLORER_SHELL_LAUNCH_PHASE_DESKTOP (1u << 3)
#define DESKPANE_EXPLORER_SHELL_LAUNCH_PHASE_DOCUMENT (1u << 4)
#define DESKPANE_EXPLORER_SHELL_LAUNCH_PHASE_APPLICATION (1u << 5)
#define DESKPANE_EXPLORER_SHELL_LAUNCH_PHASE_EXECUTE (1u << 6)

#define DESKPANE_QUICK_ACCESS_OPERATION_QUERY_PIN_STATE 1u
#define DESKPANE_QUICK_ACCESS_OPERATION_PIN 2u
#define DESKPANE_QUICK_ACCESS_OPERATION_UNPIN 3u

#define DESKPANE_QUICK_ACCESS_PIN_STATE_UNKNOWN 0u
#define DESKPANE_QUICK_ACCESS_PIN_STATE_NOT_PINNED 1u
#define DESKPANE_QUICK_ACCESS_PIN_STATE_PINNED 2u

#define DESKPANE_QUICK_ACCESS_PHASE_COM_INITIALIZE (1u << 0)
#define DESKPANE_QUICK_ACCESS_PHASE_CREATE_OBJECT (1u << 1)
#define DESKPANE_QUICK_ACCESS_PHASE_QUICK_NAMESPACE (1u << 2)
#define DESKPANE_QUICK_ACCESS_PHASE_ITEMS (1u << 3)
#define DESKPANE_QUICK_ACCESS_PHASE_ENUMERATE (1u << 4)
#define DESKPANE_QUICK_ACCESS_PHASE_ITEM_PATH (1u << 5)
#define DESKPANE_QUICK_ACCESS_PHASE_PROPERTY (1u << 6)
#define DESKPANE_QUICK_ACCESS_PHASE_PARENT_NAMESPACE (1u << 7)
#define DESKPANE_QUICK_ACCESS_PHASE_PARSE_NAME (1u << 8)
#define DESKPANE_QUICK_ACCESS_PHASE_INVOKE (1u << 9)

#define DESKPANE_RECYCLE_BIN_OPERATION_QUERY 1u
#define DESKPANE_RECYCLE_BIN_OPERATION_RESTORE 2u

#define DESKPANE_RECYCLE_BIN_PHASE_COM_INITIALIZE (1u << 0)
#define DESKPANE_RECYCLE_BIN_PHASE_CREATE_OBJECT (1u << 1)
#define DESKPANE_RECYCLE_BIN_PHASE_NAMESPACE (1u << 2)
#define DESKPANE_RECYCLE_BIN_PHASE_ITEMS (1u << 3)
#define DESKPANE_RECYCLE_BIN_PHASE_ENUMERATE (1u << 4)
#define DESKPANE_RECYCLE_BIN_PHASE_ITEM_NAME (1u << 5)
#define DESKPANE_RECYCLE_BIN_PHASE_PROPERTY (1u << 6)
#define DESKPANE_RECYCLE_BIN_PHASE_INVOKE (1u << 7)

#define DESKPANE_SHORTCUT_DEFAULT_RESOLVE_TIMEOUT_MS 3000u
#define DESKPANE_SHORTCUT_MAX_RESOLVE_TIMEOUT_MS 65535u
#define DESKPANE_SHORTCUT_MAX_INPUT_PATH_CHARS 32767u
#define DESKPANE_SHORTCUT_MAX_INPUT_VALUE_CHARS 32767u
#define DESKPANE_EXPLORER_SHELL_LAUNCH_MAX_INPUT_CHARS 32767u
#define DESKPANE_QUICK_ACCESS_MAX_INPUT_CHARS 32767u
#define DESKPANE_RECYCLE_BIN_MAX_INPUT_CHARS 32767u
// Deep review R17 (G2 audit): mirrors DESKPANE_MUSIC_VOLUME_MAX_INPUT_CHARS
// in lib.rs and MusicVolumeNativeBackend — the header was the one of the
// three mirror sites missing it.
#define DESKPANE_MUSIC_VOLUME_MAX_INPUT_CHARS 32767u

#define DESKPANE_NATIVE_UTF16_BUFFER_V1_SIZE_64 16u
#define DESKPANE_NATIVE_UTF16_STRING_V1_SIZE_64 16u
#define DESKPANE_SHORTCUT_READ_REQUEST_V2_SIZE_64 144u
#define DESKPANE_SHORTCUT_READ_RESULT_V2_SIZE_64 136u
#define DESKPANE_SHORTCUT_RESOLVE_REQUEST_V2_SIZE_64 192u
#define DESKPANE_SHORTCUT_WRITE_REQUEST_V2_SIZE_64 144u
#define DESKPANE_SHORTCUT_WRITE_RESULT_V2_SIZE_64 96u
#define DESKPANE_SHORTCUT_UI_RESOLVE_REQUEST_V2_SIZE_64 64u
#define DESKPANE_SHORTCUT_UI_RESOLVE_RESULT_V2_SIZE_64 64u
#define DESKPANE_MUSIC_VOLUME_REQUEST_V1_SIZE_64 88u
#define DESKPANE_MUSIC_VOLUME_RESULT_V1_SIZE_64 104u
#define DESKPANE_EXPLORER_SHELL_LAUNCH_REQUEST_V1_SIZE_64 96u
#define DESKPANE_EXPLORER_SHELL_LAUNCH_RESULT_V1_SIZE_64 88u
#define DESKPANE_QUICK_ACCESS_REQUEST_V1_SIZE_64 96u
#define DESKPANE_QUICK_ACCESS_RESULT_V1_SIZE_64 112u
#define DESKPANE_RECYCLE_BIN_REQUEST_V1_SIZE_64 80u
#define DESKPANE_RECYCLE_BIN_RESULT_V1_SIZE_64 104u

#if defined(_WIN32)
#define DESKPANE_NATIVE_API __declspec(dllimport)
#define DESKPANE_NATIVE_CALL __cdecl
#else
#define DESKPANE_NATIVE_API
#define DESKPANE_NATIVE_CALL
#endif

typedef struct DeskPaneNativeUtf16BufferV1 {
    uint16_t* data;
    uint32_t capacity_chars;
    uint32_t reserved0;
} DeskPaneNativeUtf16BufferV1;

typedef struct DeskPaneNativeUtf16StringV1 {
    const uint16_t* data;
    uint32_t length_chars;
    uint32_t reserved0;
} DeskPaneNativeUtf16StringV1;

typedef struct DeskPaneShortcutReadRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t mode;
    uint32_t flags;
    const uint16_t* shortcut_path;
    uint32_t shortcut_path_length_chars;
    uint32_t reserved0;
    DeskPaneNativeUtf16BufferV1 target_path;
    DeskPaneNativeUtf16BufferV1 description;
    DeskPaneNativeUtf16BufferV1 arguments;
    DeskPaneNativeUtf16BufferV1 working_directory;
    DeskPaneNativeUtf16BufferV1 icon_path;
    uint64_t reserved[4];
} DeskPaneShortcutReadRequestV2;

typedef struct DeskPaneShortcutReadResultV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t load_hresult;
    int32_t resolve_hresult;
    uint32_t attempted_fields;
    uint32_t succeeded_fields;
    uint32_t present_fields;
    uint32_t caller_buffer_too_small_fields;
    uint32_t source_truncated_fields;
    int32_t target_hresult;
    int32_t description_hresult;
    int32_t arguments_hresult;
    int32_t working_directory_hresult;
    int32_t icon_hresult;
    int32_t icon_index;
    uint32_t target_required_chars;
    uint32_t description_required_chars;
    uint32_t arguments_required_chars;
    uint32_t working_directory_required_chars;
    uint32_t icon_required_chars;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskPaneShortcutReadResultV2;

typedef struct DeskPaneShortcutResolveRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t timeout_ms;
    uint32_t flags;
    DeskPaneShortcutReadRequestV2 read_request;
    uint64_t reserved[4];
} DeskPaneShortcutResolveRequestV2;

typedef struct DeskPaneShortcutWriteRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t flags;
    int32_t icon_index;
    DeskPaneNativeUtf16StringV1 shortcut_path;
    DeskPaneNativeUtf16StringV1 target_path;
    DeskPaneNativeUtf16StringV1 description;
    DeskPaneNativeUtf16StringV1 arguments;
    DeskPaneNativeUtf16StringV1 working_directory;
    DeskPaneNativeUtf16StringV1 icon_path;
    uint64_t reserved[4];
} DeskPaneShortcutWriteRequestV2;

typedef struct DeskPaneShortcutWriteResultV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t save_hresult;
    uint32_t attempted_fields;
    uint32_t succeeded_fields;
    int32_t target_hresult;
    int32_t description_hresult;
    int32_t arguments_hresult;
    int32_t working_directory_hresult;
    int32_t icon_hresult;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskPaneShortcutWriteResultV2;

typedef struct DeskPaneShortcutUiResolveRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t flags;
    uint32_t reserved0;
    DeskPaneNativeUtf16StringV1 shortcut_path;
    uint64_t owner_hwnd;
    uint64_t reserved[3];
} DeskPaneShortcutUiResolveRequestV2;

typedef struct DeskPaneShortcutUiResolveResultV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t load_hresult;
    int32_t resolve_hresult;
    uint32_t resolve_flags;
    uint64_t reserved[3];
} DeskPaneShortcutUiResolveResultV2;

typedef struct DeskPaneMusicVolumeRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t operation;
    uint32_t flags;
    DeskPaneNativeUtf16StringV1 source_app_user_model_id;
    DeskPaneNativeUtf16StringV1 source_display_name;
    double volume;
    uint64_t reserved[4];
} DeskPaneMusicVolumeRequestV1;

typedef struct DeskPaneMusicVolumeResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    uint32_t match_kind;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t device_hresult;
    int32_t system_hresult;
    int32_t session_hresult;
    uint32_t has_session_volume;
    uint32_t operation_succeeded;
    uint32_t reserved0;
    double system_volume;
    double session_volume;
    uint64_t reserved[4];
} DeskPaneMusicVolumeResultV1;

typedef struct DeskPaneExplorerShellLaunchRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t flags;
    uint32_t reserved0;
    DeskPaneNativeUtf16StringV1 path;
    DeskPaneNativeUtf16StringV1 working_directory;
    DeskPaneNativeUtf16StringV1 verb;
    uint64_t reserved[4];
} DeskPaneExplorerShellLaunchRequestV1;

typedef struct DeskPaneExplorerShellLaunchResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t windows_hresult;
    int32_t desktop_hresult;
    int32_t document_hresult;
    int32_t application_hresult;
    int32_t execute_hresult;
    uint32_t operation_succeeded;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskPaneExplorerShellLaunchResultV1;

typedef struct DeskPaneQuickAccessRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t operation;
    uint32_t flags;
    DeskPaneNativeUtf16StringV1 folder_path;
    DeskPaneNativeUtf16StringV1 parent_path;
    DeskPaneNativeUtf16StringV1 folder_name;
    uint64_t reserved[4];
} DeskPaneQuickAccessRequestV1;

typedef struct DeskPaneQuickAccessResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t quick_namespace_hresult;
    int32_t items_hresult;
    int32_t enumerate_hresult;
    int32_t item_path_hresult;
    int32_t property_hresult;
    int32_t parent_namespace_hresult;
    int32_t parse_name_hresult;
    int32_t invoke_hresult;
    uint32_t pin_state;
    uint32_t operation_succeeded;
    uint32_t matched_item;
    uint32_t fallback_used;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskPaneQuickAccessResultV1;

typedef struct DeskPaneRecycleBinRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t operation;
    uint32_t flags;
    DeskPaneNativeUtf16StringV1 original_parent;
    DeskPaneNativeUtf16StringV1 original_name;
    uint64_t reserved[4];
} DeskPaneRecycleBinRequestV1;

typedef struct DeskPaneRecycleBinResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t namespace_hresult;
    int32_t items_hresult;
    int32_t enumerate_hresult;
    int32_t item_name_hresult;
    int32_t property_hresult;
    int32_t invoke_hresult;
    uint32_t matched_count;
    uint32_t restored_count;
    uint32_t operation_succeeded;
    uint32_t reserved0;
    uint32_t reserved1;
    uint64_t reserved[4];
} DeskPaneRecycleBinResultV1;

#if UINTPTR_MAX == UINT64_MAX
#if defined(__cplusplus)
static_assert(sizeof(DeskPaneNativeUtf16BufferV1) == DESKPANE_NATIVE_UTF16_BUFFER_V1_SIZE_64,
              "DeskPaneNativeUtf16BufferV1 ABI size changed");
static_assert(sizeof(DeskPaneNativeUtf16StringV1) == DESKPANE_NATIVE_UTF16_STRING_V1_SIZE_64,
              "DeskPaneNativeUtf16StringV1 ABI size changed");
static_assert(sizeof(DeskPaneShortcutReadRequestV2) == DESKPANE_SHORTCUT_READ_REQUEST_V2_SIZE_64,
              "DeskPaneShortcutReadRequestV2 ABI size changed");
static_assert(sizeof(DeskPaneShortcutReadResultV2) == DESKPANE_SHORTCUT_READ_RESULT_V2_SIZE_64,
              "DeskPaneShortcutReadResultV2 ABI size changed");
static_assert(sizeof(DeskPaneShortcutResolveRequestV2) == DESKPANE_SHORTCUT_RESOLVE_REQUEST_V2_SIZE_64,
              "DeskPaneShortcutResolveRequestV2 ABI size changed");
static_assert(sizeof(DeskPaneShortcutWriteRequestV2) == DESKPANE_SHORTCUT_WRITE_REQUEST_V2_SIZE_64,
              "DeskPaneShortcutWriteRequestV2 ABI size changed");
static_assert(sizeof(DeskPaneShortcutWriteResultV2) == DESKPANE_SHORTCUT_WRITE_RESULT_V2_SIZE_64,
              "DeskPaneShortcutWriteResultV2 ABI size changed");
static_assert(sizeof(DeskPaneShortcutUiResolveRequestV2) == DESKPANE_SHORTCUT_UI_RESOLVE_REQUEST_V2_SIZE_64,
              "DeskPaneShortcutUiResolveRequestV2 ABI size changed");
static_assert(sizeof(DeskPaneShortcutUiResolveResultV2) == DESKPANE_SHORTCUT_UI_RESOLVE_RESULT_V2_SIZE_64,
              "DeskPaneShortcutUiResolveResultV2 ABI size changed");
static_assert(sizeof(DeskPaneMusicVolumeRequestV1) == DESKPANE_MUSIC_VOLUME_REQUEST_V1_SIZE_64,
              "DeskPaneMusicVolumeRequestV1 ABI size changed");
static_assert(sizeof(DeskPaneMusicVolumeResultV1) == DESKPANE_MUSIC_VOLUME_RESULT_V1_SIZE_64,
              "DeskPaneMusicVolumeResultV1 ABI size changed");
static_assert(sizeof(DeskPaneExplorerShellLaunchRequestV1) == DESKPANE_EXPLORER_SHELL_LAUNCH_REQUEST_V1_SIZE_64,
              "DeskPaneExplorerShellLaunchRequestV1 ABI size changed");
static_assert(sizeof(DeskPaneExplorerShellLaunchResultV1) == DESKPANE_EXPLORER_SHELL_LAUNCH_RESULT_V1_SIZE_64,
              "DeskPaneExplorerShellLaunchResultV1 ABI size changed");
static_assert(sizeof(DeskPaneQuickAccessRequestV1) == DESKPANE_QUICK_ACCESS_REQUEST_V1_SIZE_64,
              "DeskPaneQuickAccessRequestV1 ABI size changed");
static_assert(sizeof(DeskPaneQuickAccessResultV1) == DESKPANE_QUICK_ACCESS_RESULT_V1_SIZE_64,
              "DeskPaneQuickAccessResultV1 ABI size changed");
static_assert(sizeof(DeskPaneRecycleBinRequestV1) == DESKPANE_RECYCLE_BIN_REQUEST_V1_SIZE_64,
              "DeskPaneRecycleBinRequestV1 ABI size changed");
static_assert(sizeof(DeskPaneRecycleBinResultV1) == DESKPANE_RECYCLE_BIN_RESULT_V1_SIZE_64,
              "DeskPaneRecycleBinResultV1 ABI size changed");
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
_Static_assert(sizeof(DeskPaneNativeUtf16BufferV1) == DESKPANE_NATIVE_UTF16_BUFFER_V1_SIZE_64,
               "DeskPaneNativeUtf16BufferV1 ABI size changed");
_Static_assert(sizeof(DeskPaneNativeUtf16StringV1) == DESKPANE_NATIVE_UTF16_STRING_V1_SIZE_64,
               "DeskPaneNativeUtf16StringV1 ABI size changed");
_Static_assert(sizeof(DeskPaneShortcutReadRequestV2) == DESKPANE_SHORTCUT_READ_REQUEST_V2_SIZE_64,
               "DeskPaneShortcutReadRequestV2 ABI size changed");
_Static_assert(sizeof(DeskPaneShortcutReadResultV2) == DESKPANE_SHORTCUT_READ_RESULT_V2_SIZE_64,
               "DeskPaneShortcutReadResultV2 ABI size changed");
_Static_assert(sizeof(DeskPaneShortcutResolveRequestV2) == DESKPANE_SHORTCUT_RESOLVE_REQUEST_V2_SIZE_64,
               "DeskPaneShortcutResolveRequestV2 ABI size changed");
_Static_assert(sizeof(DeskPaneShortcutWriteRequestV2) == DESKPANE_SHORTCUT_WRITE_REQUEST_V2_SIZE_64,
               "DeskPaneShortcutWriteRequestV2 ABI size changed");
_Static_assert(sizeof(DeskPaneShortcutWriteResultV2) == DESKPANE_SHORTCUT_WRITE_RESULT_V2_SIZE_64,
               "DeskPaneShortcutWriteResultV2 ABI size changed");
_Static_assert(sizeof(DeskPaneShortcutUiResolveRequestV2) == DESKPANE_SHORTCUT_UI_RESOLVE_REQUEST_V2_SIZE_64,
               "DeskPaneShortcutUiResolveRequestV2 ABI size changed");
_Static_assert(sizeof(DeskPaneShortcutUiResolveResultV2) == DESKPANE_SHORTCUT_UI_RESOLVE_RESULT_V2_SIZE_64,
               "DeskPaneShortcutUiResolveResultV2 ABI size changed");
_Static_assert(sizeof(DeskPaneMusicVolumeRequestV1) == DESKPANE_MUSIC_VOLUME_REQUEST_V1_SIZE_64,
               "DeskPaneMusicVolumeRequestV1 ABI size changed");
_Static_assert(sizeof(DeskPaneMusicVolumeResultV1) == DESKPANE_MUSIC_VOLUME_RESULT_V1_SIZE_64,
               "DeskPaneMusicVolumeResultV1 ABI size changed");
_Static_assert(sizeof(DeskPaneExplorerShellLaunchRequestV1) == DESKPANE_EXPLORER_SHELL_LAUNCH_REQUEST_V1_SIZE_64,
               "DeskPaneExplorerShellLaunchRequestV1 ABI size changed");
_Static_assert(sizeof(DeskPaneExplorerShellLaunchResultV1) == DESKPANE_EXPLORER_SHELL_LAUNCH_RESULT_V1_SIZE_64,
               "DeskPaneExplorerShellLaunchResultV1 ABI size changed");
_Static_assert(sizeof(DeskPaneQuickAccessRequestV1) == DESKPANE_QUICK_ACCESS_REQUEST_V1_SIZE_64,
               "DeskPaneQuickAccessRequestV1 ABI size changed");
_Static_assert(sizeof(DeskPaneQuickAccessResultV1) == DESKPANE_QUICK_ACCESS_RESULT_V1_SIZE_64,
               "DeskPaneQuickAccessResultV1 ABI size changed");
_Static_assert(sizeof(DeskPaneRecycleBinRequestV1) == DESKPANE_RECYCLE_BIN_REQUEST_V1_SIZE_64,
               "DeskPaneRecycleBinRequestV1 ABI size changed");
_Static_assert(sizeof(DeskPaneRecycleBinResultV1) == DESKPANE_RECYCLE_BIN_RESULT_V1_SIZE_64,
               "DeskPaneRecycleBinResultV1 ABI size changed");
#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_native_abi_version(void);
DESKPANE_NATIVE_API uint64_t DESKPANE_NATIVE_CALL deskpane_native_capabilities(void);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_shortcut_read_v2(
    const DeskPaneShortcutReadRequestV2* request,
    DeskPaneShortcutReadResultV2* result);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_shortcut_resolve_no_ui_v2(
    const DeskPaneShortcutResolveRequestV2* request,
    DeskPaneShortcutReadResultV2* result);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_shortcut_write_v2(
    const DeskPaneShortcutWriteRequestV2* request,
    DeskPaneShortcutWriteResultV2* result);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_shortcut_resolve_with_ui_v2(
    const DeskPaneShortcutUiResolveRequestV2* request,
    DeskPaneShortcutUiResolveResultV2* result);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_music_volume_v1(
    const DeskPaneMusicVolumeRequestV1* request,
    DeskPaneMusicVolumeResultV1* result);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_explorer_shell_launch_v1(
    const DeskPaneExplorerShellLaunchRequestV1* request,
    DeskPaneExplorerShellLaunchResultV1* result);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_quick_access_v1(
    const DeskPaneQuickAccessRequestV1* request,
    DeskPaneQuickAccessResultV1* result);
DESKPANE_NATIVE_API uint32_t DESKPANE_NATIVE_CALL deskpane_recycle_bin_v1(
    const DeskPaneRecycleBinRequestV1* request,
    DeskPaneRecycleBinResultV1* result);

#ifdef __cplusplus
}
#endif

#endif
