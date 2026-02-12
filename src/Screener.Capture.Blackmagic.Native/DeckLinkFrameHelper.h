#pragma once

#ifdef DECKLINK_NATIVE_EXPORTS
#define DECKLINK_API __declspec(dllexport)
#else
#define DECKLINK_API __declspec(dllimport)
#endif

extern "C" {
    // Copy frame bytes from a DeckLink video input frame
    // framePtr: Raw COM interface pointer to IDeckLinkVideoInputFrame
    // buffer: Destination buffer
    // bufferSize: Size of data to copy
    // Returns: 1 if successful, 0 otherwise
    DECKLINK_API int CopyDeckLinkFrameBytes(void* framePtr, void* buffer, int bufferSize);

    // Get frame information
    // Returns: 1 if successful, 0 otherwise
    DECKLINK_API int GetDeckLinkFrameInfo(void* framePtr, int* width, int* height, int* rowBytes, unsigned int* flags);
}
