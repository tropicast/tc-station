#import "AudioBridge.m"
#include <assert.h>

int main(void) {
    @autoreleasepool {
        UInt32 channels = 99;
        AudioBufferList list = {0};
        UInt32 header = (UInt32)offsetof(AudioBufferList, mBuffers);
        assert(TCInputChannels(&list, header, &channels));
        assert(channels == 0);
        assert(TCInputChannels(&list, sizeof(UInt32), &channels));
        assert(!TCInputChannels(&list, sizeof(UInt32) - 1, &channels));
        list.mNumberBuffers = 1;
        list.mBuffers[0].mNumberChannels = 2;
        assert(!TCInputChannels(&list, header, &channels));
        assert(TCInputChannels(&list, sizeof(list), &channels));
        assert(channels == 2);
        list.mBuffers[0].mNumberChannels = 9;
        assert(!TCInputChannels(&list, sizeof(list), &channels));
        list.mNumberBuffers = UINT32_MAX;
        assert(!TCInputChannels(&list, sizeof(list), &channels));
        int error = 0;
        char *json = tc_audio_list(&error);
        assert(json && error == 0);
        NSData *data = [[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding];
        NSArray *devices = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
        assert(devices);
        for (NSDictionary *device in devices) {
            assert(CFGetTypeID((__bridge CFTypeRef)device[@"default"]) == CFBooleanGetTypeID());
            assert(CFGetTypeID((__bridge CFTypeRef)device[@"loopback"]) == CFBooleanGetTypeID());
        }
        tc_audio_free(json);
        puts("Native Core Audio buffer-layout checks passed.");
    }
    return 0;
}
