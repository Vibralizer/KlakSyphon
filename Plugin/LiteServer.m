// KlakSyphon - Syphon plugin for Unity
// https://github.com/keijiro/KlakSyphon

// LiteServer: Simplified implementation of Syphon server

#import "LiteServer.h"
#import "SyphonServerConnectionManager.h"
#import "SyphonPrivate.h"
#import <Metal/MTLDevice.h>
#import <Metal/MTLTexture.h>

@interface LiteServer()
{
    NSString *_name;
    NSString *_uuid;
    MTLPixelFormat _pixelFormat;
    IOSurfaceRef _ioSurface;
    id <MTLTexture> _texture;
    SyphonServerConnectionManager *_connection;

    // Resize support. A new surface is announced to clients only after it has
    // received a few publishes, because a publish notifies clients as soon as
    // the host has encoded its blit, before the GPU has run it; a fresh
    // surface announced on its first publish reads as empty. Replaced
    // surfaces wait in a release queue for a few more publishes, because the
    // host may still have GPU work in flight on them.
    IOSurfaceRef _announcedSurface;    // owned separately only while != _ioSurface
    id <MTLTexture> _announcedTexture; // set only while != _ioSurface
    int _publishesIntoCurrent;
    NSMutableArray *_releaseQueue;
}

@property (readonly) NSDictionary *description;

@end

@implementation LiteServer

- (id)init
{
    [self doesNotRecognizeSelector:_cmd];
    return nil;
}

- (id)initWithName:(NSString *)name dimensions:(NSSize)size pixelFormat:(MTLPixelFormat)format device:(id <MTLDevice>)device
{
    if (self = [super init])
    {
        _name = [name copy];
        _uuid = SyphonCreateUUIDString();
        _pixelFormat = format;

        _ioSurface = [self newSurfaceWithSize:size device:device texture:&_texture];
        _announcedSurface = _ioSurface;
        _releaseQueue = [NSMutableArray array];

        _connection = [[SyphonServerConnectionManager alloc] initWithUUID:_uuid options:nil];
        if (_ioSurface) [_connection setSurfaceID:IOSurfaceGetID(_ioSurface)];
        [_connection start];

        [self startBroadcasts];
    }
    return self;
}

- (void)dealloc
{
    [self stopBroadcasts];

    if (_connection) [_connection stop];

    if (_announcedSurface && _announcedSurface != _ioSurface) CFRelease(_announcedSurface);
    if (_ioSurface) CFRelease(_ioSurface);
    [_releaseQueue removeAllObjects];
}

#pragma mark - Public method

- (BOOL)hasClients
{
    return _connection.hasClients;
}

// Swaps in a surface of the new size, keeping the server's UUID and its
// connection, so clients stay bound to this server and rebind to the new
// surface (SyphonMessageTypeUpdateSurfaceID) once it holds rendered frames.
- (BOOL)resizeTo:(NSSize)size device:(id <MTLDevice>)device
{
    if (_ioSurface &&
        IOSurfaceGetWidth(_ioSurface) == (size_t)size.width &&
        IOSurfaceGetHeight(_ioSurface) == (size_t)size.height) return YES;

    id <MTLTexture> texture = nil;
    IOSurfaceRef surface = [self newSurfaceWithSize:size device:device texture:&texture];
    if (!surface) return NO;

    // The surface clients know (and its texture) stays alive until its
    // replacement is announced; one never announced goes straight to the queue.
    if (_ioSurface && _ioSurface != _announcedSurface)
        [self enqueueRelease:_ioSurface texture:_texture];
    else
        _announcedTexture = _texture;

    _ioSurface = surface;
    _texture = texture;
    _publishesIntoCurrent = 0;
    return YES;
}

- (void)publishNewFrame
{
    if (_publishesIntoCurrent < 3) _publishesIntoCurrent++;

    // Unity queues at most two frames, so by the third publish into a new
    // surface the first frame rendered into it has completed on the GPU.
    if (_ioSurface != _announcedSurface && _publishesIntoCurrent >= 3)
    {
        [_connection setSurfaceID:IOSurfaceGetID(_ioSurface)];
        if (_announcedSurface) [self enqueueRelease:_announcedSurface texture:_announcedTexture];
        _announcedSurface = _ioSurface;
        _announcedTexture = nil;
    }

    [_connection publishNewFrame];

    // Release replaced surfaces a few publishes after they left service.
    for (NSInteger i = (NSInteger)_releaseQueue.count - 1; i >= 0; i--)
    {
        NSMutableDictionary *entry = _releaseQueue[i];
        int left = [entry[@"left"] intValue] - 1;
        if (left <= 0) [_releaseQueue removeObjectAtIndex:i];
        else entry[@"left"] = @(left);
    }
}

#pragma mark - Private method

- (IOSurfaceRef)newSurfaceWithSize:(NSSize)size device:(id <MTLDevice>)device texture:(id <MTLTexture> __strong *)texture
{
    if (size.width < 1 || size.height < 1 || !device) return NULL;

    NSDictionary *attribs = @{ (NSString *)kIOSurfaceIsGlobal: @YES,
                               (NSString *)kIOSurfaceWidth: @(size.width),
                               (NSString *)kIOSurfaceHeight: @(size.height),
                               (NSString *)kIOSurfacePixelFormat: @(kCVPixelFormatType_32BGRA),
                               (NSString *)kIOSurfaceBytesPerElement: @4u };
    IOSurfaceRef surface = IOSurfaceCreate((CFDictionaryRef)attribs);
    if (!surface) return NULL;

    MTLTextureDescriptor *desc = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:_pixelFormat
                                                                                    width:size.width
                                                                                   height:size.height
                                                                                mipmapped:NO];
    desc.usage = MTLTextureUsageRenderTarget | MTLTextureUsageShaderRead;
    *texture = [device newTextureWithDescriptor:desc iosurface:surface plane:0];
    if (!*texture)
    {
        CFRelease(surface);
        return NULL;
    }
    return surface;
}

// Takes over the caller's reference to the surface. The queue entry owns the
// surface (and its texture, when given) until it is dropped.
- (void)enqueueRelease:(IOSurfaceRef)surface texture:(id <MTLTexture>)texture
{
    NSMutableDictionary *entry = [NSMutableDictionary dictionary];
    entry[@"surface"] = (__bridge_transfer id)surface;
    if (texture) entry[@"texture"] = texture;
    entry[@"left"] = @4;
    [_releaseQueue addObject:entry];
}

- (NSDictionary *)description
{
    NSDictionary *surface = _connection.surfaceDescription;
    if (!surface) surface = [NSDictionary dictionary];

    // Getting the app name: helper tasks, command-line tools, etc, don't have a NSRunningApplication instance,
    // so fall back to NSProcessInfo in those cases, then use an empty string as a last resort.
    // http://developer.apple.com/library/mac/qa/qa1544/_index.html
    NSString *appName = [[NSRunningApplication currentApplication] localizedName];
    if (!appName) appName = [[NSProcessInfo processInfo] processName];
    if (!appName) appName = [NSString string];

    return @{ SyphonServerDescriptionDictionaryVersionKey: @(kSyphonDictionaryVersion),
              SyphonServerDescriptionNameKey: _name,
              SyphonServerDescriptionUUIDKey: _uuid,
              SyphonServerDescriptionAppNameKey: appName,
              SyphonServerDescriptionSurfacesKey: @[ surface ] };
}

#pragma mark - Notification handling

- (void)startBroadcasts
{
    [NSDistributedNotificationCenter.defaultCenter addObserver:self
                                                      selector:@selector(handleDiscoveryRequest:)
                                                          name:SyphonServerAnnounceRequest
                                                        object:nil];
    [self postNotification:SyphonServerAnnounce];
}

- (void)stopBroadcasts
{
    [NSDistributedNotificationCenter.defaultCenter removeObserver:self];
    [self postNotification:SyphonServerRetire];
}

- (void)handleDiscoveryRequest:(NSNotification *)aNotification
{
    [self postNotification:SyphonServerAnnounce];
}

- (void)postNotification:(NSString *)notificationName
{
    NSDictionary *description = self.description;
    [NSDistributedNotificationCenter.defaultCenter postNotificationName:notificationName
                                                                 object:description[SyphonServerDescriptionUUIDKey]
                                                               userInfo:description
                                                     deliverImmediately:YES];
}

@end
