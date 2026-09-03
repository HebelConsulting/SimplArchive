namespace SimplArchive.Localization;

/// <summary>
/// Turns an API <c>errorCode</c> into text for the user, in their language.
/// </summary>
/// <remarks>
/// The API's Problem Details <c>detail</c> is <b>English</b> — 153 exception classes carry their message as a
/// constructor literal, so no <c>Accept-Language</c> handling can reach them (the request-localization middleware
/// only governs the server-rendered pages). Both clients used to display <c>detail</c> verbatim, so a German user
/// got German until something went wrong and English exactly when it mattered (issue #423/#424).
///
/// The fix is not to translate the API. The <c>errorCode</c> is already the stable contract — tests assert on it,
/// and ADR 0543 makes codes and rel names the compatibility surface, precisely so that prose can change without
/// breaking anyone. So the code crosses the wire and the CLIENT owns the words. That also keeps the API's payload
/// language-neutral, which is what a machine-readable interface should be; <c>detail</c> goes back to being a
/// developer-facing explanation rather than user copy.
///
/// The mapping is an explicit switch rather than <c>Strings.Get("ApiErr_" + code)</c>, and that is deliberate: a
/// computed key is invisible to <c>LocalizationKeyTests</c>, which scans for literal key names. Spelling every key
/// out keeps the four languages guarded. An unmapped code falls back to a generic localised sentence — never to
/// the English <c>detail</c>, because a fallback that leaks English is the bug this exists to remove.
///
/// The keys on the left are the codes the API ACTUALLY emits, which is not always what the exception class is
/// called: <c>DocumentUnderLegalHoldException</c> emits <c>LEGAL_HOLD</c>, and the invalid-transition one emits
/// <c>INVALID_WORKFLOW_TRANSITION</c>. Two mappings here were originally written from the class names and so
/// could never fire — silently, because an unmapped code still produces a sensible generic sentence.
/// <c>ApiErrorCodesExistTests</c> now fails the build on a code the API never emits.
///
/// Guarded on three sides: <c>NoServerDetailInClientsTests</c> fails the build if a client reads <c>detail</c>
/// again, and <c>WebApiErrorLocalizationTests</c> / <c>DesktopApiErrorLocalizationTests</c> drive each client in
/// German against a real server refusal and assert the German sentence reaches the user.
/// </remarks>
public static class ApiErrorText
{
    public static string For(string? errorCode) => errorCode switch
    {
        // Mail-domain registration (#667). The not-verified one is the interesting case: it is the EXPECTED
        // answer between publishing a record and DNS carrying it, so the sentence says what to do next rather
        // than reporting a fault.
        "INVALID_MAIL_DOMAIN" => Strings.Get("ApiErrInvalidMailDomain"),
        "MAIL_DOMAIN_ALREADY_CLAIMED" => Strings.Get("ApiErrMailDomainAlreadyClaimed"),
        "MAIL_DOMAIN_NOT_VERIFIED" => Strings.Get("ApiErrMailDomainNotVerified"),
        "UNSUPPORTED_UPLOAD_CONTENT" => Strings.Get("ApiErrUnsupportedUploadContent"),
        "EXTERNAL_LINKS_DISABLED" => Strings.Get("ApiErrExternalLinksDisabled"),
        "EXTERNAL_LINK_URL_NOT_SHOWN" => Strings.Get("ApiErrExternalLinkUrlNotShown"),
        "INSUFFICIENT_RIGHTS_TO_GRANT" => Strings.Get("ApiErrInsufficientRightsToGrant"),
        "LEGAL_HOLD" => Strings.Get("ApiErrDocumentUnderLegalHold"),
        "MAIL_ROUTING_RIGHT_REQUIRED" => Strings.Get("ApiErrMailRoutingRightRequired"),
        "USER_ADDRESS_CLAIM_NOT_ALLOWED" => Strings.Get("ApiErrUserAddressClaimNotAllowed"),
        "PERSONAL_SPACE_STRUCTURE" => Strings.Get("ApiErrPersonalSpaceStructure"),
        "DOCUMENT_CHECKED_OUT" => Strings.Get("ApiErrDocumentCheckedOut"),
        "ETAG_MISMATCH" => Strings.Get("ApiErrEtagMismatch"),
        "IF_MATCH_REQUIRED" => Strings.Get("ApiErrIfMatchRequired"),
        "STORAGE_QUOTA_EXCEEDED" => Strings.Get("ApiErrStorageQuotaExceeded"),
        "INVALID_WORKFLOW_TRANSITION" => Strings.Get("ApiErrWorkflowTransitionNotAllowed"),
        "CANNOT_CHANGE_ROOT_INHERITANCE" => Strings.Get("ApiErrCannotChangeRootInheritance"),
        "INTRAY_PAGES_NOT_SUPPORTED" => Strings.Get("ApiErrIntrayPagesNotSupported"),
        "INTRAY_ITEM_HAS_NO_PAGES" => Strings.Get("ApiErrIntrayItemHasNoPages"),
        "INTRAY_ITEM_HAS_NO_PAGES_TO_SPLIT" => Strings.Get("ApiErrIntrayNoPagesToSplit"),
        "INTRAY_JOIN_NEEDS_SEVERAL_ITEMS" => Strings.Get("ApiErrIntrayJoinNeedsSeveral"),
        "INTRAY_JOIN_NOT_HOMOGENEOUS" => Strings.Get("ApiErrIntrayJoinNotHomogeneous"),
        "INTRAY_PAGE_ORDER_INVALID" => Strings.Get("ApiErrIntrayPageOrderInvalid"),
        // The check-out working-copy page operations (ADR 0593) — same rules, same words.
        "CHECKOUT_PAGE_ORDER_INVALID" => Strings.Get("ApiErrIntrayPageOrderInvalid"),
        "CHECKOUT_PAGES_NOT_SUPPORTED" => Strings.Get("ApiErrIntrayPagesNotSupported"),
        "CHECKOUT_WORKING_COPY_SIGNED" => Strings.Get("ApiErrWorkingCopySigned"),
        "INTRAY_NO_PATCH_CODES_FOUND" => Strings.Get("ApiErrIntrayNoPatchCodes"),
        // The raw-item disclosure's two refusals (#648, ADR 0643). Both name the line to fix, which is the
        // whole value of surfacing them in an editor whose premise is that the user can see what they edit.
        // The inventory-booking primitive (ADR 0735). The slot conflict is the one users will actually
        // meet; the other two are backstops a conforming client never triggers (the rel gates them).
        "BOOKING_SLOT_CONFLICT" => Strings.Get("ApiErrBookingSlotConflict"),
        "RESOURCE_NOT_BOOKABLE" => Strings.Get("ApiErrResourceNotBookable"),
        "BOOKING_SLOT_INVALID" => Strings.Get("ApiErrBookingSlotInvalid"),
        "UNPARSABLE_ITEM_SOURCE" => Strings.Get("ApiErrUnparsableItemSource"),
        "ITEM_SOURCE_UID_CHANGED" => Strings.Get("ApiErrItemSourceUidChanged"),
        _ => Strings.Get("ApiErrGeneric"),
    };
}
