import {
  IsString,
  IsOptional,
  MaxLength,
} from 'class-validator';

/**
 * RegisterWorkstationDto
 * 
 * Workstation registration is now device-only.
 * Employee resolution happens at event ingestion time via NT account (sam_account_name).
 */
export class RegisterWorkstationDto {
  @IsString()
  @MaxLength(255)
  @IsOptional()
  workstation_name?: string;

  @IsString()
  @MaxLength(100)
  @IsOptional()
  os_version?: string;

  @IsString()
  @MaxLength(50)
  @IsOptional()
  agent_version?: string;

  @IsString()
  @IsOptional()
  notes?: string;
}

