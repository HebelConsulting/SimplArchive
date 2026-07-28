// Saves bytes fetched by the authenticated Blazor HttpClient as a file download — used for zip archive
// entries, which the Api proxies (they aren't presigned storage objects). See ADR "Zip file browsing".
window.downloadFileFromStream = async (fileName, streamRef) => {
    const buffer = await streamRef.arrayBuffer();
    const blob = new Blob([buffer]);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName ?? 'download';
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};
