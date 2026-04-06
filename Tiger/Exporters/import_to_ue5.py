import unreal
import os
import json


class CharmImporter:
    def __init__(self, folder_path: str, b_unique_folder: bool) -> None:
        self.folder_path = folder_path
        script_name = os.path.basename(__file__)
        info_name = f"{script_name.split('_')[0]}_info.cfg"
        self.config = json.load(open(os.path.join(self.folder_path, info_name)))
        if b_unique_folder:
            self.content_path = f"{self.config['UnrealInteropPath']}/{self.config['MeshName']}"
        else:
            self.content_path = f"{self.config['UnrealInteropPath']}"
        if not unreal.EditorAssetLibrary.does_directory_exist(self.content_path):
            unreal.EditorAssetLibrary.make_directory(self.content_path)

    def import_entity(self):
        self.make_materials()
        self.import_entity_mesh()
        self.assign_entity_materials()
        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def import_static(self):
        self.make_materials()
        self.import_static_mesh(combine=True)
        self.assign_static_materials()
        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def import_map(self):
        self.make_materials()
        self.import_static_mesh(combine=False)
        self.assign_map_materials()
        self.assemble_map()
        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def assemble_map(self) -> None:
        # Load existing map level or create a new one
        map_path = f'/Game/{self.content_path}/map'
        if unreal.EditorAssetLibrary.does_asset_exist(map_path):
            unreal.EditorLevelLibrary.load_level(map_path)
        else:
            unreal.EditorLevelLibrary.new_level(map_path)
            # Add default scene lighting only for new maps
            unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.DirectionalLight, location=[0, 0, 10000], rotation=unreal.Rotator(-50, -30, 0))
            unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SkyLight, location=[0, 0, 10000])
            unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SkyAtmosphere, location=[0, 0, 0])
            unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.ExponentialHeightFog, location=[0, 0, 0])

        import re

        # Build part name -> mesh hash mapping from Parts config
        part_to_hash = {}
        for mesh_hash, parts_dict in self.config.get("Parts", {}).items():
            if isinstance(parts_dict, dict):
                for part_name in parts_dict.keys():
                    part_to_hash[part_name] = mesh_hash

        static_names = {}
        for x in unreal.EditorAssetLibrary.list_assets(f'/Game/{self.content_path}/Statics/', recursive=False):
            asset_name = x.split('/')[-1].split('.')[0]
            # Strip UE5 duplicate suffixes (_ncl1_N)
            clean_name = re.sub(r'_ncl\d+_\d+$', '', asset_name)

            # Look up mesh hash via Parts config (authoritative)
            name = part_to_hash.get(clean_name)
            if name is None:
                # Fallback: extract hash from name pattern Hash_GroupN_...
                if "Group" in clean_name:
                    name = clean_name.split("_")[0]
                else:
                    name = clean_name

            if name not in static_names:
                static_names[name] = []
            static_names[name].append(x)

        for static, instances in self.config["Instances"].items():
            try:  # fix this
                parts = static_names[static]
            except:
                print(f"Failed on {static}")
                continue
            for part in parts:
                sm = unreal.EditorAssetLibrary.load_asset(part)
                for instance in instances:
                    quat = unreal.Quat(instance["Rotation"][0], instance["Rotation"][1], instance["Rotation"][2], instance["Rotation"][3])
                    euler = quat.euler()
                    rotator = unreal.Rotator(-euler.x+180, -euler.y+180, -euler.z)
                    location = [-instance["Translation"][0]*100, instance["Translation"][1]*100, instance["Translation"][2]*100]
                    s = unreal.EditorLevelLibrary.spawn_actor_from_object(sm, location=location, rotation=rotator)  # l must be UE4 Object
                    # Scale can be either a scalar (1.3.2) or [x,y,z] array (2.4.7+)
                    scale = instance['Scale']
                    if isinstance(scale, list):
                        s.set_actor_label(s.get_actor_label() + f"_{scale[0]}")
                        s.set_actor_relative_scale3d(scale)
                    else:
                        s.set_actor_label(s.get_actor_label() + f"_{scale}")
                        s.set_actor_relative_scale3d([scale]*3)

        unreal.EditorLevelLibrary.save_current_level()

    def assign_map_materials(self) -> None:
        import re
        for x in unreal.EditorAssetLibrary.list_assets(f'/Game/{self.content_path}/Statics/', recursive=False):
            mesh = unreal.load_asset(x)
            mesh_materials = mesh.get_editor_property("static_materials")
            new_mesh_materials = []
            for skeletal_material in mesh_materials:
                slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
                # Strip UE5 duplicate suffix (_ncl1_N)
                mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
                mat_asset = unreal.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{mat_hash}")
                if mat_asset:
                    skeletal_material.set_editor_property("material_interface", mat_asset)
                new_mesh_materials.append(skeletal_material)
            mesh.set_editor_property("static_materials", new_mesh_materials)

    def assign_static_materials(self) -> None:
        import re
        mesh = unreal.load_asset(f"/Game/{self.content_path}/{self.config['MeshName']}")
        mesh_materials = mesh.get_editor_property("static_materials")
        new_mesh_materials = []
        for skeletal_material in mesh_materials:
            slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
            mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
            mat_asset = unreal.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{mat_hash}")
            if mat_asset:
                skeletal_material.set_editor_property("material_interface", mat_asset)
            new_mesh_materials.append(skeletal_material)
        mesh.set_editor_property("static_materials", new_mesh_materials)

    def assign_entity_materials(self) -> None:
        import re
        mesh = unreal.load_asset(f"/Game/{self.content_path}/{self.config['MeshName']}")
        mesh_materials = mesh.get_editor_property("materials")
        new_mesh_materials = []
        for skeletal_material in mesh_materials:
            slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
            mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
            mat_asset = unreal.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{mat_hash}")
            if mat_asset:
                skeletal_material.set_editor_property("material_interface", mat_asset)
            new_mesh_materials.append(skeletal_material)
        mesh.set_editor_property("materials", new_mesh_materials)

    def import_entity_mesh(self) -> None:
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", f"/Game/{self.content_path}/")
        task.set_editor_property("filename", f"{self.folder_path}/{self.config['MeshName']}.fbx")
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", True)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', True)
        # todo fix this, not static mesh import data
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('combine_meshes', False)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', True)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)  # todo add nanite option
        task.set_editor_property("options", options)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    def import_static_mesh(self, combine) -> None:
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", f"/Game/{self.content_path}/Statics/")
        task.set_editor_property("filename", f"{self.folder_path}/{self.config['MeshName']}.fbx")
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", True)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', False)
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('import_uniform_scale', 100.0)
        options.static_mesh_import_data.set_editor_property('combine_meshes', combine)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', True)
        options.static_mesh_import_data.set_editor_property('normal_import_method', unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)  # todo add nanite option
        task.set_editor_property("options", options)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    def make_materials(self) -> None:
        # Get all materials we need
        materials = list(self.config["Materials"].keys())

        for mat in materials:
            mat_path = f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{mat}"
            # Skip if material already exists
            if unreal.EditorAssetLibrary.does_asset_exist(mat_path):
                continue
            material = self.make_material(mat)
            unreal.MaterialEditingLibrary.recompile_material(material)

    def make_material(self, matstr: str) -> unreal.Material:
        # Make base material
        material = unreal.AssetToolsHelpers.get_asset_tools().create_asset("M_" + matstr, f"/Game/{self.config['UnrealInteropPath']}/Materials", unreal.Material, unreal.MaterialFactoryNew())

        if os.path.exists(f"{self.folder_path}/Shaders/Unreal/PS_{matstr}.usf"):
            # Add textures
            texture_samples = self.add_textures(material, matstr)

            # Add custom node
            custom_node = self.add_custom_node(material, texture_samples, matstr)

            # Set output, not using in-built custom expression system because I want to leave it open for manual control
            self.create_output(material, custom_node)
        else:
            material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
            const = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionConstant, -300, 0)
            unreal.MaterialEditingLibrary.connect_material_property(const, "", unreal.MaterialProperty.MP_OPACITY_MASK)

        return material

    def create_output(self, material: unreal.Material, custom_node: unreal.MaterialExpressionCustom) -> None:
        mat_att = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionBreakMaterialAttributes, -300, 0)
        # Connect custom node to the new break
        unreal.MaterialEditingLibrary.connect_material_expressions(custom_node, '', mat_att, 'Attr')
        # Connect all outputs
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "BaseColor", unreal.MaterialProperty.MP_BASE_COLOR)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Metallic", unreal.MaterialProperty.MP_METALLIC)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Roughness", unreal.MaterialProperty.MP_ROUGHNESS)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "EmissiveColor", unreal.MaterialProperty.MP_EMISSIVE_COLOR)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "OpacityMask", unreal.MaterialProperty.MP_OPACITY_MASK)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Normal", unreal.MaterialProperty.MP_NORMAL)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "AmbientOcclusion", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION)

    def add_custom_node(self, material: unreal.Material, texture_samples: list, matstr: str) -> unreal.MaterialExpressionCustom:
        import re as _re

        all_cfg_indices = sorted([int(x) for x in self.config["Materials"][matstr]["Textures"]["PS"].keys()])

        custom_node = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionCustom, -500, 0)

        code = open(f"{self.folder_path}/Shaders/Unreal/PS_{matstr}.usf", "r").read()

        if "// masked" in code:
            material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
            material.set_editor_property("two_sided", True)

        # Scan shader code for actually-referenced Material_Texture2D positions
        used_positions = sorted(set(int(m.group(1)) for m in _re.finditer(r"Material_Texture2D_(\d+)(?:\.|Sampler)", code)))
        # Map positional references back to original cfg indices
        kept_indices = [all_cfg_indices[pos] for pos in used_positions if pos < len(all_cfg_indices)]

        inputs = []
        for seq in range(len(kept_indices)):
            ci = unreal.CustomInput()
            ci.set_editor_property('input_name', f't{seq}')
            inputs.append(ci)
        ci = unreal.CustomInput()
        ci.set_editor_property('input_name', 'tx')
        inputs.append(ci)
        vc = unreal.CustomInput()
        vc.set_editor_property('input_name', 'vc')
        inputs.append(vc)
        vcw = unreal.CustomInput()
        vcw.set_editor_property('input_name', 'vcw')
        inputs.append(vcw)
        viewdir = unreal.CustomInput()
        viewdir.set_editor_property('input_name', 'viewDir')
        inputs.append(viewdir)

        custom_node.set_editor_property('code', code)
        custom_node.set_editor_property('inputs', inputs)
        custom_node.set_editor_property('output_type', unreal.CustomMaterialOutputType.CMOT_MATERIAL_ATTRIBUTES)

        for seq, orig in enumerate(kept_indices):
            if orig in texture_samples:
                unreal.MaterialEditingLibrary.connect_material_expressions(texture_samples[orig], 'RGBA', custom_node, f't{seq}')

        texcoord = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTextureCoordinate, -500, 400)
        unreal.MaterialEditingLibrary.connect_material_expressions(texcoord, '', custom_node, 'tx')

        vertex_color = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionVertexColor, -500, 500)
        unreal.MaterialEditingLibrary.connect_material_expressions(vertex_color, '', custom_node, 'vc')
        unreal.MaterialEditingLibrary.connect_material_expressions(vertex_color, 'A', custom_node, 'vcw')

        cam_vec = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionCameraVectorWS, -500, 700)
        vec_transform = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTransform, -500, 800)
        vec_transform.set_editor_property('transform_source_type', unreal.MaterialVectorCoordTransformSource.TRANSFORMSOURCE_WORLD)
        vec_transform.set_editor_property('transform_type', unreal.MaterialVectorCoordTransform.TRANSFORM_TANGENT)
        unreal.MaterialEditingLibrary.connect_material_expressions(cam_vec, '', vec_transform, '')
        unreal.MaterialEditingLibrary.connect_material_expressions(vec_transform, '', custom_node, 'viewDir')

        return custom_node

    def add_textures(self,  material: unreal.Material, matstr: str) -> dict:
        texture_samples = {}

        # Import texture list for the material

        tex_factory = unreal.TextureFactory()
        tex_factory.set_editor_property('supported_class', unreal.Texture2D)
        names = [f"{self.folder_path}/Textures/{texstruct['Hash']}.dds" for i, texstruct in self.config["Materials"][matstr]["Textures"]["PS"].items()]
        srgbs = {int(i): texstruct['SRGB'] for i, texstruct in self.config["Materials"][matstr]["Textures"]["PS"].items()}
        import_tasks = []
        for name in names:
            asset_import_task = unreal.AssetImportTask()
            asset_import_task.set_editor_property('filename', name)
            asset_import_task.set_editor_property('destination_path', f'/Game/{self.content_path}/Textures')
            asset_import_task.set_editor_property('save', True)
            asset_import_task.set_editor_property('replace_existing', False)  # dont do extra work if we dont need to
            asset_import_task.set_editor_property('automated', True)
            import_tasks.append(asset_import_task)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(import_tasks)

        # Make texture samples
        for i, texstruct in self.config["Materials"][matstr]["Textures"]["PS"].items():
            i = int(i)
            texture_sample = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTextureSample, -1000, -500 + 250 * i)

            ts_TextureUePath = f"/Game/{self.content_path}/Textures/{texstruct['Hash']}.{texstruct['Hash']}"
            ts_LoadedTexture = unreal.EditorAssetLibrary.load_asset(ts_TextureUePath)
            if not ts_LoadedTexture:  # some cubemaps and 3d textures cannot be loaded for now
                continue
            ts_LoadedTexture.set_editor_property('srgb', srgbs[i])
            if srgbs[i] == True:
                ts_LoadedTexture.set_editor_property('compression_settings', unreal.TextureCompressionSettings.TC_DEFAULT)
            else:
                ts_LoadedTexture.set_editor_property('compression_settings', unreal.TextureCompressionSettings.TC_VECTOR_DISPLACEMENTMAP)

            texture_sample.set_editor_property('texture', ts_LoadedTexture)
            if texstruct['SRGB'] == True:
                texture_sample.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_COLOR)
            else:
                texture_sample.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_COLOR)
            texture_samples[i] = texture_sample

            unreal.EditorAssetLibrary.save_loaded_asset(ts_LoadedTexture)

        return texture_samples

    """
    Updates all materials used by this model to the latest .usfs found in the Shaders/ folder.
    Very useful for improving the material quality without much manual work.
    """
    def update_material_code(self) -> None:
        # Get all materials to update
        materials = list(self.config["Materials"].keys())

        # For each material, find the code node and update it
        mats = {unreal.EditorAssetLibrary.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{matstr}"): matstr for matstr in materials}
        it = unreal.ObjectIterator()
        for x in it:
            if x.get_outer() in mats:
                if isinstance(x, unreal.MaterialExpressionCustom):
                    code = open(f"{self.folder_path}/Shaders/Unreal/PS_{mats[x.get_outer()]}.usf", "r").read()
                    x.set_editor_property('code', code)
                    print(f"Updated material {mats[x.get_outer()]}")

        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/Materials/", False)


if __name__ == "__main__":
    importer = CharmImporter(os.path.dirname(os.path.realpath(__file__)), b_unique_folder=False)
    importer.import_entity()
    # importer.update_material_code()
