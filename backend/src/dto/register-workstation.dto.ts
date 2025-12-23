import {
  IsString,
  IsOptional,
  MaxLength,
} from 'class-validator';
import { ApiPropertyOptional } from '@nestjs/swagger';

/**
 * RegisterWorkstationDto
 * 
 * Workstation registration is now device-only.
 * Employee resolution happens at event ingestion time via NT account (sam_account_name).
 */
export class RegisterWorkstationDto {
  @ApiPropertyOptional({
    description: 'Workstation name/identifier',
    example: 'WS-001',
    maxLength: 255,
  })
  @IsString()
  @MaxLength(255)
  @IsOptional()
  workstation_name?: string;

  @ApiPropertyOptional({
    description: 'Operating system version',
    example: 'Windows 11 Pro 22H2',
    maxLength: 100,
  })
  @IsString()
  @MaxLength(100)
  @IsOptional()
  os_version?: string;

  @ApiPropertyOptional({
    description: 'Desktop Agent version',
    example: '1.0.0',
    maxLength: 50,
  })
  @IsString()
  @MaxLength(50)
  @IsOptional()
  agent_version?: string;

  @ApiPropertyOptional({
    description: 'Additional notes',
    example: 'Main office workstation',
  })
  @IsString()
  @IsOptional()
  notes?: string;
}

