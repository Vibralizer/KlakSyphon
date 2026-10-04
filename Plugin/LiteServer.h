// KlakSyphon - Syphon plugin for Unity
// https://github.com/keijiro/KlakSyphon

// LiteServer: Simplified implementation of Syphon server

#import <Metal/MTLPixelFormat.h>

@protocol MTLDevice;
@protocol MTLTexture;

@interface LiteServer : NSObject

@property (readonly) id <MTLTexture> texture;
@property (readonly) BOOL hasClients;

- (id)initWithName:(NSString *)name dimensions:(NSSize)size pixelFormat:(MTLPixelFormat)format device:(id <MTLDevice>)device;
- (BOOL)resizeTo:(NSSize)size device:(id <MTLDevice>)device;
- (void)publishNewFrame;

@end
