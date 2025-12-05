import { Injectable, CanActivate, ExecutionContext, UnauthorizedException } from '@nestjs/common';

/**
 * JwtAuthGuard
 * 
 * Placeholder for JWT authentication.
 * Full implementation will be done in Week 5.
 * 
 * For now, this guard will be a placeholder that can be extended
 * with Passport JWT strategy in Week 5.
 */
@Injectable()
export class JwtAuthGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const request = context.switchToHttp().getRequest();
    
    // TODO: Implement JWT validation in Week 5
    // For now, check if Authorization header exists
    const authHeader = request.headers['authorization'];
    
    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      throw new UnauthorizedException('Missing or invalid authorization token');
    }
    
    // Extract token (will be validated in Week 5)
    const token = authHeader.substring(7);
    
    // Placeholder: In Week 5, validate JWT token and attach user to request
    // For now, just check token exists
    if (!token) {
      throw new UnauthorizedException('Invalid token');
    }
    
    // TODO: Validate JWT and attach user to request
    // request.user = decodedJwt;
    
    return true;
  }
}

