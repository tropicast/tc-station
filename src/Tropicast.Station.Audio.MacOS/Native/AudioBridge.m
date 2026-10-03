#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <AudioToolbox/AudioToolbox.h>
#import <CoreAudio/CoreAudio.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <CoreMedia/CoreMedia.h>
#include <stddef.h>

typedef void (*TCPcmCallback)(const void *, int);
typedef void (*TCErrorCallback)(int);

static OSStatus TCRead(AudioObjectID object, AudioObjectPropertySelector selector,
                       AudioObjectPropertyScope scope, UInt32 *size, void *value) {
    AudioObjectPropertyAddress address = {selector, scope, kAudioObjectPropertyElementMain};
    return AudioObjectGetPropertyData(object, &address, 0, NULL, size, value);
}

static NSString *TCString(AudioObjectID device, AudioObjectPropertySelector selector) {
    CFStringRef value = NULL;
    UInt32 size = sizeof(value);
    if (TCRead(device, selector, kAudioObjectPropertyScopeGlobal, &size, &value) != noErr || !value) {
        return nil;
    }
    return CFBridgingRelease(value);
}

static BOOL TCInputChannels(const AudioBufferList *list, UInt32 size, UInt32 *channels) {
    size_t header = offsetof(AudioBufferList, mBuffers);
    *channels = 0;
    if (size < sizeof(list->mNumberBuffers)) {
        return NO;
    }
    if (!list->mNumberBuffers) {
        return YES;
    }
    if (size < header || list->mNumberBuffers > (size - header) / sizeof(AudioBuffer)) {
        return NO;
    }
    for (UInt32 b = 0; b < list->mNumberBuffers; b++) {
        if (list->mBuffers[b].mNumberChannels > 8 - *channels) {
            return NO;
        }
        *channels += list->mBuffers[b].mNumberChannels;
    }
    return YES;
}

char *tc_audio_list(int *error) {
    @autoreleasepool {
        *error = 0;
        AudioObjectPropertyAddress address = {
            kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
        };
        UInt32 size = 0;
        if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &address, 0, NULL, &size) != noErr) {
            *error = 8;
            return NULL;
        }
        NSMutableData *data = [NSMutableData dataWithLength:size];
        if (TCRead(kAudioObjectSystemObject, kAudioHardwarePropertyDevices,
                   kAudioObjectPropertyScopeGlobal, &size, data.mutableBytes) != noErr) {
            *error = 8;
            return NULL;
        }
        AudioDeviceID defaultInput = kAudioObjectUnknown;
        UInt32 defaultSize = sizeof(defaultInput);
        if (TCRead(kAudioObjectSystemObject, kAudioHardwarePropertyDefaultInputDevice,
                   kAudioObjectPropertyScopeGlobal, &defaultSize, &defaultInput) != noErr) {
            *error = 8;
            return NULL;
        }
        NSMutableArray *result = [NSMutableArray array];
        AudioDeviceID *devices = data.mutableBytes;
        for (UInt32 i = 0; i < size / sizeof(AudioDeviceID); i++) {
            AudioDeviceID device = devices[i];
            UInt32 alive = 0, aliveSize = sizeof(alive);
            if (TCRead(device, kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal,
                       &aliveSize, &alive) != noErr || !alive) {
                continue;
            }
            AudioObjectPropertyAddress input = {
                kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeInput, kAudioObjectPropertyElementMain
            };
            UInt32 bufferSize = 0;
            if (AudioObjectGetPropertyDataSize(device, &input, 0, NULL, &bufferSize) != noErr) {
                *error = 8;
                return NULL;
            }
            if (bufferSize < sizeof(UInt32)) {
                *error = 5;
                return NULL;
            }
            NSMutableData *buffers = [NSMutableData dataWithLength:bufferSize];
            if (AudioObjectGetPropertyData(device, &input, 0, NULL, &bufferSize, buffers.mutableBytes) != noErr) {
                *error = 8;
                return NULL;
            }
            AudioBufferList *list = buffers.mutableBytes;
            UInt32 channels = 0;
            if (!TCInputChannels(list, bufferSize, &channels)) {
                *error = 5;
                return NULL;
            }
            if (!channels) {
                continue;
            }
            Float64 rate = 0;
            UInt32 rateSize = sizeof(rate);
            NSString *uid = TCString(device, kAudioDevicePropertyDeviceUID);
            NSString *name = TCString(device, kAudioObjectPropertyName);
            if (!uid || !name || TCRead(device, kAudioDevicePropertyNominalSampleRate,
                kAudioObjectPropertyScopeGlobal, &rateSize, &rate) != noErr) {
                *error = 8;
                return NULL;
            }
            if (channels > 8 || rate < 8000 || rate > 192000 || rate != (int)rate) {
                *error = 5;
                return NULL;
            }
            [result addObject:@{@"id":uid, @"name":name, @"rate":@((int)rate),
                @"channels":@(channels), @"default":device == defaultInput ? @YES : @NO, @"loopback":@NO}];
        }
        if (@available(macOS 13.0, *)) {
            [result addObject:@{@"id":@"tropicast:system-audio", @"name":@"System audio (ScreenCaptureKit)",
                @"rate":@48000, @"channels":@2, @"default":@YES, @"loopback":@YES}];
        }
        NSData *json = [NSJSONSerialization dataWithJSONObject:result options:0 error:nil];
        if (!json) {
            *error = 8;
            return NULL;
        }
        char *text = malloc(json.length + 1);
        if (!text) {
            *error = 8;
            return NULL;
        }
        memcpy(text, json.bytes, json.length);
        text[json.length] = '\0';
        return text;
    }
}

void tc_audio_free(void *pointer) { free(pointer); }

@interface TCCapture : NSObject <SCStreamOutput, SCStreamDelegate>
@property(nonatomic) AudioQueueRef audioQueue;
@property(nonatomic, strong) SCStream *stream;
@property(nonatomic, strong) dispatch_queue_t callbacks;
@property(nonatomic) TCPcmCallback pcm;
@property(nonatomic) TCErrorCallback failure;
@property(atomic) BOOL stopping;
@property(atomic) BOOL started;
@property(nonatomic) int rate;
@property(nonatomic) int channels;
- (int)startInput:(NSString *)uid;
- (int)startSystem;
- (int)stop;
@end

static void TCQueueState(void *context, AudioQueueRef queue, AudioQueuePropertyID property) {
    (void)property;
    TCCapture *capture = (__bridge TCCapture *)context;
    dispatch_async(capture.callbacks, ^{
        if (capture.started && !capture.stopping && capture.audioQueue == queue && capture.failure) {
            UInt32 running = 0, size = sizeof(running);
            if (AudioQueueGetProperty(queue, kAudioQueueProperty_IsRunning, &running, &size) != noErr || !running) {
                capture.failure(4);
            }
        }
    });
}

@implementation TCCapture
- (int)startInput:(NSString *)uid {
    if (![NSBundle.mainBundle objectForInfoDictionaryKey:@"NSMicrophoneUsageDescription"]) {
        return 6;
    }
    AVAuthorizationStatus status = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio];
    if (status == AVAuthorizationStatusNotDetermined) {
        dispatch_semaphore_t permission = dispatch_semaphore_create(0);
        __block BOOL allowed = NO;
        [AVCaptureDevice requestAccessForMediaType:AVMediaTypeAudio completionHandler:^(BOOL granted) {
            allowed = granted;
            dispatch_semaphore_signal(permission);
        }];
        dispatch_semaphore_wait(permission, DISPATCH_TIME_FOREVER);
        if (!allowed) {
            return 1;
        }
    } else if (status != AVAuthorizationStatusAuthorized) {
        return 1;
    }
    AudioStreamBasicDescription format = {
        .mSampleRate = self.rate, .mFormatID = kAudioFormatLinearPCM,
        .mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
        .mBytesPerPacket = self.channels * 4, .mFramesPerPacket = 1,
        .mBytesPerFrame = self.channels * 4, .mChannelsPerFrame = self.channels, .mBitsPerChannel = 32
    };
    __weak TCCapture *weakSelf = self;
    AudioQueueRef queue = NULL;
    OSStatus result = AudioQueueNewInputWithDispatchQueue(&queue, &format, 0, self.callbacks,
        ^(AudioQueueRef q, AudioQueueBufferRef buffer, const AudioTimeStamp *time,
          UInt32 packets, const AudioStreamPacketDescription *descriptions) {
            (void)time; (void)packets; (void)descriptions;
            TCCapture *capture = weakSelf;
            if (!capture || capture.stopping) {
                return;
            }
            if (buffer->mAudioDataByteSize && capture.pcm) {
                capture.pcm(buffer->mAudioData, (int)buffer->mAudioDataByteSize);
            }
            if (!capture.stopping && AudioQueueEnqueueBuffer(q, buffer, 0, NULL) != noErr && capture.failure) {
                capture.failure(4);
            }
        });
    if (result != noErr) {
        return 8;
    }
    self.audioQueue = queue;
    CFStringRef device = (__bridge CFStringRef)uid;
    if (AudioQueueSetProperty(queue, kAudioQueueProperty_CurrentDevice, &device, sizeof(device)) != noErr) {
        return 4;
    }
    if (AudioQueueAddPropertyListener(queue, kAudioQueueProperty_IsRunning, TCQueueState,
                                     (__bridge void *)self) != noErr) {
        return 8;
    }
    UInt32 bytes = MAX(1, self.rate / 50) * self.channels * 4;
    for (int i = 0; i < 3; i++) {
        AudioQueueBufferRef buffer = NULL;
        if (AudioQueueAllocateBuffer(queue, bytes, &buffer) != noErr ||
            AudioQueueEnqueueBuffer(queue, buffer, 0, NULL) != noErr) {
            return 8;
        }
    }
    if (AudioQueueStart(queue, NULL) != noErr) {
        return 8;
    }
    self.started = YES;
    return 0;
}

- (int)startSystem {
    if (@available(macOS 13.0, *)) {
        if (![NSBundle.mainBundle objectForInfoDictionaryKey:@"NSScreenCaptureUsageDescription"]) {
            return 6;
        }
        dispatch_semaphore_t ready = dispatch_semaphore_create(0);
        __block int result = 0;
        [SCShareableContent getShareableContentExcludingDesktopWindows:YES onScreenWindowsOnly:NO
            completionHandler:^(SCShareableContent *content, NSError *error) {
                if (error) {
                    result = 3;
                    dispatch_semaphore_signal(ready);
                    return;
                }
                SCDisplay *display = content.displays.firstObject;
                if (!display) {
                    result = 7;
                    dispatch_semaphore_signal(ready);
                    return;
                }
                SCContentFilter *filter = [[SCContentFilter alloc] initWithDisplay:display
                    excludingApplications:@[] exceptingWindows:@[]];
                SCStreamConfiguration *configuration = [[SCStreamConfiguration alloc] init];
                configuration.capturesAudio = YES;
                configuration.excludesCurrentProcessAudio = YES;
                configuration.sampleRate = self.rate;
                configuration.channelCount = self.channels;
                // No video output is registered or exposed; minimize SCK's internal display work.
                configuration.width = 2;
                configuration.height = 2;
                configuration.minimumFrameInterval = CMTimeMake(1, 1);
                self.stream = [[SCStream alloc] initWithFilter:filter configuration:configuration delegate:self];
                NSError *outputError = nil;
                if (![self.stream addStreamOutput:self type:SCStreamOutputTypeAudio
                                    sampleHandlerQueue:self.callbacks error:&outputError]) {
                    result = 8;
                    dispatch_semaphore_signal(ready);
                    return;
                }
                [self.stream startCaptureWithCompletionHandler:^(NSError *startError) {
                    result = startError ? 3 : 0;
                    dispatch_semaphore_signal(ready);
                }];
            }];
        dispatch_semaphore_wait(ready, DISPATCH_TIME_FOREVER);
        return result;
    }
    return 2;
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error {
    (void)stream; (void)error;
    dispatch_async(self.callbacks, ^{
        if (!self.stopping && self.failure) {
            self.failure(8);
        }
    });
}

- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sample ofType:(SCStreamOutputType)type {
    (void)stream;
    if (self.stopping || type != SCStreamOutputTypeAudio || !CMSampleBufferDataIsReady(sample)) {
        return;
    }
    const AudioStreamBasicDescription *format = CMAudioFormatDescriptionGetStreamBasicDescription(
        CMSampleBufferGetFormatDescription(sample));
    if (!format || format->mFormatID != kAudioFormatLinearPCM ||
        !(format->mFormatFlags & kAudioFormatFlagIsFloat) || format->mBitsPerChannel != 32 ||
        format->mSampleRate != self.rate || format->mChannelsPerFrame != (UInt32)self.channels) {
        if (self.failure) self.failure(5);
        return;
    }
    size_t required = 0;
    CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sample, &required, NULL, 0, NULL, NULL, 0, NULL);
    AudioBufferList *list = malloc(required);
    CMBlockBufferRef block = NULL;
    if (!list || CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sample, NULL, list, required,
        NULL, NULL, kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment, &block) != noErr) {
        free(list);
        if (block) CFRelease(block);
        if (self.failure) self.failure(5);
        return;
    }
    CMItemCount count = CMSampleBufferGetNumSamples(sample);
    BOOL planar = (format->mFormatFlags & kAudioFormatFlagIsNonInterleaved) != 0;
    BOOL valid = count > 0 && count <= self.rate * 2 &&
        list->mNumberBuffers == (planar ? (UInt32)self.channels : 1);
    for (UInt32 b = 0; valid && b < list->mNumberBuffers; b++) {
        valid = list->mBuffers[b].mData &&
            list->mBuffers[b].mDataByteSize >= count * sizeof(float) * (planar ? 1 : self.channels);
    }
    if (valid) {
        size_t bytes = count * self.channels * sizeof(float);
        float *interleaved = malloc(bytes);
        if (interleaved) {
            if (planar) {
                for (CMItemCount frame = 0; frame < count; frame++) {
                    for (int channel = 0; channel < self.channels; channel++) {
                        interleaved[frame * self.channels + channel] = ((float *)list->mBuffers[channel].mData)[frame];
                    }
                }
            } else {
                memcpy(interleaved, list->mBuffers[0].mData, bytes);
            }
            if (self.pcm) self.pcm(interleaved, (int)bytes);
            free(interleaved);
        } else if (self.failure) {
            self.failure(8);
        }
    } else if (self.failure) {
        self.failure(5);
    }
    if (block) CFRelease(block);
    free(list);
}

- (int)stop {
    self.stopping = YES;
    int result = 0;
    if (self.audioQueue) {
        AudioQueueRemovePropertyListener(self.audioQueue, kAudioQueueProperty_IsRunning, TCQueueState, (__bridge void *)self);
        if (AudioQueueStop(self.audioQueue, true) != noErr) result = 8;
        if (AudioQueueDispose(self.audioQueue, true) != noErr) result = 8;
        self.audioQueue = NULL;
    }
    if (self.stream) {
        dispatch_semaphore_t stopped = dispatch_semaphore_create(0);
        __block BOOL failed = NO;
        [self.stream stopCaptureWithCompletionHandler:^(NSError *error) {
            // Retain the native delegate until a delayed completion, even if shutdown times out.
            self.started = NO;
            failed = error != nil;
            dispatch_semaphore_signal(stopped);
        }];
        if (dispatch_semaphore_wait(stopped, dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_SEC)) != 0 || failed) {
            result = 8;
        }
        NSError *error = nil;
        if (![self.stream removeStreamOutput:self type:SCStreamOutputTypeAudio error:&error]) result = 8;
        self.stream = nil;
    }
    // All managed callbacks run on this queue; drain it before releasing delegate roots.
    dispatch_sync(self.callbacks, ^{
        self.pcm = NULL;
        self.failure = NULL;
    });
    return result;
}
@end

int tc_audio_start(const char *identifier, int rate, int channels,
                   TCPcmCallback pcm, TCErrorCallback failure, void **handle) {
    @autoreleasepool {
        *handle = NULL;
        TCCapture *capture = [[TCCapture alloc] init];
        capture.rate = rate;
        capture.channels = channels;
        capture.pcm = pcm;
        capture.failure = failure;
        capture.callbacks = dispatch_queue_create("tropicast.audio.callbacks", DISPATCH_QUEUE_SERIAL);
        NSString *uid = [NSString stringWithUTF8String:identifier];
        int result = [uid isEqualToString:@"tropicast:system-audio"] ? [capture startSystem] : [capture startInput:uid];
        if (result) {
            [capture stop];
            return result;
        }
        *handle = (__bridge_retained void *)capture;
        return 0;
    }
}

int tc_audio_stop(void *handle) {
    @autoreleasepool {
        TCCapture *capture = CFBridgingRelease(handle);
        return [capture stop];
    }
}
